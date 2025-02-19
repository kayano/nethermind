// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Logging;
using Nethermind.State.Tracing;
using Nethermind.Trie.Pruning;

namespace Nethermind.State
{
    /// <summary>
    /// Manages persistent storage allowing for snapshotting and restoring
    /// Persists data to ITrieStore
    /// </summary>
    internal class PersistentStorageProvider : PartialStorageProviderBase
    {
        private readonly ITrieStore _trieStore;
        private readonly StateProvider _stateProvider;
        private readonly ILogManager? _logManager;
        internal readonly IStorageTreeFactory _storageTreeFactory;
        private readonly ResettableDictionary<AddressAsKey, StorageTree> _storages = new();

        /// <summary>
        /// EIP-1283
        /// </summary>
        private readonly ResettableDictionary<StorageCell, byte[]> _originalValues = new();

        private readonly ResettableHashSet<StorageCell> _committedThisRound = new();

        public PersistentStorageProvider(ITrieStore? trieStore, StateProvider? stateProvider, ILogManager? logManager, IStorageTreeFactory? storageTreeFactory = null)
            : base(logManager)
        {
            _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
            _stateProvider = stateProvider ?? throw new ArgumentNullException(nameof(stateProvider));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _storageTreeFactory = storageTreeFactory ?? new StorageTreeFactory();
        }

        public Hash256 StateRoot { get; set; } = null!;

        /// <summary>
        /// Reset the storage state
        /// </summary>
        public override void Reset()
        {
            base.Reset();
            _storages.Reset();
            _originalValues.Clear();
            _committedThisRound.Clear();
        }

        /// <summary>
        /// Get the current value at the specified location
        /// </summary>
        /// <param name="storageCell">Storage location</param>
        /// <returns>Value at location</returns>
        protected override ReadOnlySpan<byte> GetCurrentValue(in StorageCell storageCell) =>
            TryGetCachedValue(storageCell, out byte[]? bytes) ? bytes! : LoadFromTree(storageCell);

        /// <summary>
        /// Return the original persistent storage value from the storage cell
        /// </summary>
        /// <param name="storageCell"></param>
        /// <returns></returns>
        public byte[] GetOriginal(in StorageCell storageCell)
        {
            if (!_originalValues.TryGetValue(storageCell, out var value))
            {
                throw new InvalidOperationException("Get original should only be called after get within the same caching round");
            }

            if (_transactionChangesSnapshots.TryPeek(out int snapshot))
            {
                if (_intraBlockCache.TryGetValue(storageCell, out StackList<int> stack))
                {
                    if (stack.TryGetSearchedItem(snapshot, out int lastChangeIndexBeforeOriginalSnapshot))
                    {
                        return _changes[lastChangeIndexBeforeOriginalSnapshot]!.Value;
                    }
                }
            }

            return value;
        }


        /// <summary>
        /// Called by Commit
        /// Used for persistent storage specific logic
        /// </summary>
        /// <param name="tracer">Storage tracer</param>
        protected override void CommitCore(IStorageTracer tracer)
        {
            if (_logger.IsTrace) _logger.Trace("Committing storage changes");

            if (_changes[_currentPosition] is null)
            {
                throw new InvalidOperationException($"Change at current position {_currentPosition} was null when commiting {nameof(PartialStorageProviderBase)}");
            }

            if (_changes[_currentPosition + 1] is not null)
            {
                throw new InvalidOperationException($"Change after current position ({_currentPosition} + 1) was not null when commiting {nameof(PartialStorageProviderBase)}");
            }

            HashSet<Address> toUpdateRoots = new();

            bool isTracing = tracer.IsTracingStorage;
            Dictionary<StorageCell, ChangeTrace>? trace = null;
            if (isTracing)
            {
                trace = new Dictionary<StorageCell, ChangeTrace>();
            }

            for (int i = 0; i <= _currentPosition; i++)
            {
                Change change = _changes[_currentPosition - i];
                if (!isTracing && change!.ChangeType == ChangeType.JustCache)
                {
                    continue;
                }

                if (_committedThisRound.Contains(change!.StorageCell))
                {
                    if (isTracing && change.ChangeType == ChangeType.JustCache)
                    {
                        trace![change.StorageCell] = new ChangeTrace(change.Value, trace[change.StorageCell].After);
                    }

                    continue;
                }

                if (isTracing && change.ChangeType == ChangeType.JustCache)
                {
                    tracer!.ReportStorageRead(change.StorageCell);
                }

                _committedThisRound.Add(change.StorageCell);

                if (change.ChangeType == ChangeType.Destroy)
                {
                    continue;
                }

                int forAssertion = _intraBlockCache[change.StorageCell].Pop();
                if (forAssertion != _currentPosition - i)
                {
                    throw new InvalidOperationException($"Expected checked value {forAssertion} to be equal to {_currentPosition} - {i}");
                }

                switch (change.ChangeType)
                {
                    case ChangeType.Destroy:
                        break;
                    case ChangeType.JustCache:
                        break;
                    case ChangeType.Update:
                        if (_logger.IsTrace)
                        {
                            _logger.Trace($"  Update {change.StorageCell.Address}_{change.StorageCell.Index} V = {change.Value.ToHexString(true)}");
                        }

                        SaveToTree(toUpdateRoots, change);

                        if (isTracing)
                        {
                            trace![change.StorageCell] = new ChangeTrace(change.Value);
                        }

                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }

            // TODO: it seems that we are unnecessarily recalculating root hashes all the time in storage?
            foreach (Address address in toUpdateRoots)
            {
                // since the accounts could be empty accounts that are removing (EIP-158)
                if (_stateProvider.AccountExists(address))
                {
                    Hash256 root = RecalculateRootHash(address);

                    // _logger.Warn($"Recalculating storage root {address}->{root} ({toUpdateRoots.Count})");
                    _stateProvider.UpdateStorageRoot(address, root);
                }
            }

            base.CommitCore(tracer);
            _originalValues.Reset();
            _committedThisRound.Reset();

            if (isTracing)
            {
                ReportChanges(tracer!, trace!);
            }
        }

        private void SaveToTree(HashSet<Address> toUpdateRoots, Change change)
        {
            if (_originalValues.TryGetValue(change.StorageCell, out byte[] initialValue) &&
                initialValue.AsSpan().SequenceEqual(change.Value))
            {
                // no need to update the tree if the value is the same
                return;
            }

            StorageTree tree = GetOrCreateStorage(change.StorageCell.Address);
            Db.Metrics.StorageTreeWrites++;
            toUpdateRoots.Add(change.StorageCell.Address);
            tree.Set(change.StorageCell.Index, change.Value);
        }

        /// <summary>
        /// Commit persistent storage trees
        /// </summary>
        /// <param name="blockNumber">Current block number</param>
        public void CommitTrees(long blockNumber)
        {
            // _logger.Warn($"Storage block commit {blockNumber}");
            foreach (KeyValuePair<AddressAsKey, StorageTree> storage in _storages)
            {
                storage.Value.Commit(blockNumber);
            }

            // TODO: maybe I could update storage roots only now?

            // only needed here as there is no control over cached storage size otherwise
            _storages.Reset();
        }

        private StorageTree GetOrCreateStorage(Address address)
        {
            ref StorageTree? value = ref _storages.GetValueRefOrAddDefault(address, out bool exists);
            if (!exists)
            {
                value = _storageTreeFactory.Create(address, _trieStore.GetTrieStore(address.ToAccountPath), _stateProvider.GetStorageRoot(address), StateRoot, _logManager);
                return value;
            }

            return value;
        }

        private ReadOnlySpan<byte> LoadFromTree(in StorageCell storageCell)
        {
            StorageTree tree = GetOrCreateStorage(storageCell.Address);

            Db.Metrics.StorageTreeReads++;

            if (!storageCell.IsHash)
            {
                byte[] value = tree.Get(storageCell.Index);
                PushToRegistryOnly(storageCell, value);
                return value;
            }

            return tree.Get(storageCell.Hash.Bytes);
        }

        private void PushToRegistryOnly(in StorageCell cell, byte[] value)
        {
            StackList<int> stack = SetupRegistry(cell);
            IncrementChangePosition();
            stack.Push(_currentPosition);
            _originalValues[cell] = value;
            _changes[_currentPosition] = new Change(ChangeType.JustCache, cell, value);
        }

        private static void ReportChanges(IStorageTracer tracer, Dictionary<StorageCell, ChangeTrace> trace)
        {
            foreach ((StorageCell address, ChangeTrace change) in trace)
            {
                byte[] before = change.Before;
                byte[] after = change.After;

                if (!Bytes.AreEqual(before, after))
                {
                    tracer.ReportStorageChange(address, before, after);
                }
            }
        }

        private Hash256 RecalculateRootHash(Address address)
        {
            StorageTree storageTree = GetOrCreateStorage(address);
            storageTree.UpdateRootHash();
            return storageTree.RootHash;
        }

        /// <summary>
        /// Clear all storage at specified address
        /// </summary>
        /// <param name="address">Contract address</param>
        public override void ClearStorage(Address address)
        {
            base.ClearStorage(address);

            // here it is important to make sure that we will not reuse the same tree when the contract is revived
            // by means of CREATE 2 - notice that the cached trie may carry information about items that were not
            // touched in this block, hence were not zeroed above
            // TODO: how does it work with pruning?
            _storages[address] = new StorageTree(_trieStore.GetTrieStore(address.ToAccountPath), Keccak.EmptyTreeHash, _logManager);
        }

        private class StorageTreeFactory : IStorageTreeFactory
        {
            public StorageTree Create(Address address, IScopedTrieStore trieStore, Hash256 storageRoot, Hash256 stateRoot, ILogManager? logManager)
                => new(trieStore, storageRoot, logManager);
        }
    }
}
