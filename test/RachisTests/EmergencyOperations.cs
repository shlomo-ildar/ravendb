﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FastTests.Server.JavaScript;
using Raven.Client.Http;
using Raven.Client.ServerWide;
using Raven.Server;
using Raven.Server.Config;
using Raven.Server.Documents.Patch;
using Raven.Server.ServerWide.Context;
using Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace RachisTests
{
    public class EmergencyOperations : ClusterTestBase
    {
        public EmergencyOperations(ITestOutputHelper output) : base(output)
        {
        }

        [Theory]
        [JavaScriptEngineClassData]
        public async Task LeaderCanCecedeFromClusterAndNewLeaderWillBeElected(string jsEngineType)
        {
            var customSettings = new Dictionary<string, string>();
            customSettings[RavenConfiguration.GetKey(x => x.JavaScript.EngineType)] = jsEngineType;
            
            var clusterSize = 3;
            var (_, leader) = await CreateRaftCluster(clusterSize, customSettings: customSettings);
            ClusterTopology old, @new;
            old = GetServerTopology(leader);
            new AdminJsConsole(leader, null).ApplyScript(new AdminJsScript
            (
               @"server.ServerStore.Engine.HardResetToNewCluster('A');"
            ));
            await leader.ServerStore.WaitForState(RachisState.Leader, CancellationToken.None);
            @new = GetServerTopology(leader);
            Assert.NotEqual(old.TopologyId, @new.TopologyId);
            List<Task<RavenServer>> leaderSelectedTasks = new List<Task<RavenServer>>();
            foreach (var server in Servers)
            {
                if (server == leader)
                    continue;
                leaderSelectedTasks.Add(server.ServerStore.WaitForState(RachisState.Leader, CancellationToken.None).ContinueWith(_ => server));
            }
            Assert.True(await Task.WhenAny(leaderSelectedTasks).WaitWithoutExceptionAsync(TimeSpan.FromSeconds(10)), "New leader was not elected after old leader left the cluster.");
        }

        [Theory]
        [JavaScriptEngineClassData]
        public async Task FollowerCanCecedeFromCluster(string jsEngineType)
        {
            var customSettings = new Dictionary<string, string>();
            customSettings[RavenConfiguration.GetKey(x => x.JavaScript.EngineType)] = jsEngineType;

            var clusterSize = 3;
            await CreateRaftCluster(clusterSize, customSettings: customSettings);
            var follower = Servers.First(x => x.ServerStore.CurrentRachisState == RachisState.Follower);
            ClusterTopology old, @new;
            old = GetServerTopology(follower);
            new AdminJsConsole(follower, null).ApplyScript(new AdminJsScript(@"server.ServerStore.Engine.HardResetToNewCluster('A');"));
            await follower.ServerStore.WaitForState(RachisState.Leader, CancellationToken.None);
            @new = GetServerTopology(follower);
            Assert.NotEqual(old.TopologyId, @new.TopologyId);
        }

        private static ClusterTopology GetServerTopology(RavenServer leader)
        {
            ClusterTopology old;
            using (leader.ServerStore.ContextPool.AllocateOperationContext(out TransactionOperationContext ctx))
            using (ctx.OpenReadTransaction())
            {
                old = leader.ServerStore.GetClusterTopology(ctx);
            }

            return old;
        }
    }
}
