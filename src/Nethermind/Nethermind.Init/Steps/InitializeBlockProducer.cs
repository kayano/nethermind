// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Init.Steps
{
    [RunnerStepDependencies(typeof(StartBlockProcessor), typeof(SetupKeyStore), typeof(InitializeNetwork), typeof(ReviewBlockTree))]
    public class InitializeBlockProducer : IStep
    {
        private readonly IApiWithBlockchain _api;

        public InitializeBlockProducer(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken _)
        {
            if (_api.BlockProductionPolicy!.ShouldStartBlockProduction())
            {
                _api.BlockProducer = await BuildProducer();
            }
        }

        protected virtual async Task<IBlockProducer> BuildProducer()
        {
            _api.BlockProducerEnvFactory = new BlockProducerEnvFactory(
                _api.WorldStateManager!,
                _api.BlockTree!,
                _api.SpecProvider!,
                _api.BlockValidator!,
                _api.RewardCalculatorSource!,
                _api.ReceiptStorage!,
                _api.BlockPreprocessor,
                _api.TxPool!,
                _api.TransactionComparerProvider!,
                _api.Config<IBlocksConfig>(),
                _api.LogManager);

            if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));
            IConsensusPlugin? consensusPlugin = _api.GetConsensusPlugin();

            if (consensusPlugin is not null)
            {
                IBlockProducerFactory blockProducerFactory = consensusPlugin;

                foreach (IConsensusWrapperPlugin wrapperPlugin in _api.GetConsensusWrapperPlugins().OrderBy((p) => p.Priority))
                {
                    blockProducerFactory = new ConsensusWrapperToBlockProducerFactoryAdapter(wrapperPlugin, blockProducerFactory);
                }

                return await blockProducerFactory.InitBlockProducer(consensusPlugin.DefaultBlockProductionTrigger);
            }
            else
            {
                throw new NotSupportedException($"Mining in {_api.ChainSpec.SealEngineType} mode is not supported");
            }
        }

        private class ConsensusWrapperToBlockProducerFactoryAdapter(
            IConsensusWrapperPlugin consensusWrapperPlugin,
            IBlockProducerFactory baseBlockProducerFactory) : IBlockProducerFactory
        {
            public Task<IBlockProducer> InitBlockProducer(IBlockProductionTrigger blockProductionTrigger, ITxSource? additionalTxSource = null)
            {
                return consensusWrapperPlugin.InitBlockProducer(baseBlockProducerFactory, blockProductionTrigger, additionalTxSource);
            }
        }
    }
}
