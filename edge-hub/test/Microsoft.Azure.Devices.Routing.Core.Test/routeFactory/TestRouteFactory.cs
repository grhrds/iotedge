// Copyright (c) Microsoft. All rights reserved.
namespace Microsoft.Azure.Devices.Routing.Core.Test.RouteFactory
{
    using System;
    using Microsoft.Azure.Devices.Routing.Core;

    public class TestRouteFactory : RouteFactory
    {
        public TestRouteFactory(IEndpointFactory endpointFactory)
            : base(endpointFactory)
        {
        }

        public override string IotHubName => "TestIoTHub";

        public override string GetNextRouteId() => Guid.NewGuid().ToString();
    }
}
