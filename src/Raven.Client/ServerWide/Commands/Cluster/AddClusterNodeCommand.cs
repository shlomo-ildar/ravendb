﻿using System.Net.Http;
using Raven.Client.Http;
using Sparrow.Json;

namespace Raven.Client.ServerWide.Commands.Cluster
{
    public class AddClusterNodeCommand : RavenCommand
    {
        private readonly string _url;
        private readonly string _tag;
        private readonly bool _watcher;

        public override bool IsReadRequest => false;

        public AddClusterNodeCommand(string url, string tag, bool watcher)
        {
            _url = url;
            _tag = tag;
            _watcher = watcher;
        }

        public override HttpRequestMessage CreateRequest(JsonOperationContext ctx, ServerNode node, out string url)
        {
            url = $"{node.Url}/admin/cluster/node?url={_url}&watcher={_watcher}";
            if (string.IsNullOrEmpty(_tag) == false)
            {
                url += $"&tag={_tag}";
            }
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Put
            };

            return request;
        }
    }
}
