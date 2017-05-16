﻿// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Hub.Service.Modules
{
    using System;
    using System.Threading.Tasks;
    using Autofac;
    using Microsoft.Azure.Devices.Edge.Hub.CloudProxy;
    using Microsoft.Azure.Devices.Edge.Hub.Core;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Cloud;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Routing;
    using Microsoft.Azure.Devices.Edge.Hub.Mqtt;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Routing.Core;
    using Microsoft.Azure.Devices.Routing.Core.Endpoints;
    using Microsoft.Azure.Devices.Routing.Core.TransientFaultHandling;
    using Microsoft.Extensions.Logging;
    using IRoutingMessage = Microsoft.Azure.Devices.Routing.Core.IMessage;
    using Message = Microsoft.Azure.Devices.Client.Message;

    public class RoutingModule : Module
    {
        readonly string iotHubName;

        public RoutingModule(string iotHubName)
        {
            this.iotHubName = Preconditions.CheckNonWhiteSpace(iotHubName, nameof(iotHubName));
        }

        protected override void Load(ContainerBuilder builder)
        {
            // IMessageConverter<IRoutingMessage>
            builder.Register(c => new RoutingMessageConverter())
                .As<Core.IMessageConverter<IRoutingMessage>>()
                .SingleInstance();

            // IRoutingPerfCounter
            builder.Register(c => new NullRoutingPerfCounter())
                .As<IRoutingPerfCounter>()
                .SingleInstance();

            // IRoutingUserAnalyticsLogger
            builder.Register(c => new NullUserAnalyticsLogger())
                .As<IRoutingUserAnalyticsLogger>()
                .SingleInstance();

            // IRoutingUserMetricLogger
            builder.Register(c => new NullRoutingUserMetricLogger())
                .As<IRoutingUserMetricLogger>()
                .SingleInstance();

            // IMessageConverter<Message>
            builder.Register(c => new MqttMessageConverter())
                .As<Core.IMessageConverter<Message>>()
                .SingleInstance();

            // ICloudProxyProvider
            builder.Register(c => new CloudProxyProvider(c.Resolve<Core.IMessageConverter<Message>>(), c.Resolve<ILoggerFactory>()))
                .As<ICloudProxyProvider>()
                .SingleInstance();

            // IConnectionManager
            builder.Register(c => new ConnectionManager(c.Resolve<ICloudProxyProvider>()))
                .As<IConnectionManager>()
                .SingleInstance();

            // IEndpointFactory
            builder.Register(c => new SimpleEndpointFactory(c.Resolve<IConnectionManager>(), c.Resolve<Core.IMessageConverter<IRoutingMessage>>()))
                .As<IEndpointFactory>()
                .SingleInstance();

            // IRouterFactory
            builder.Register(c => new SimpleRouteFactory(c.Resolve<IEndpointFactory>()))
                .As<IRouteFactory>()
                .SingleInstance();

            // EndpointExecutorConfig
            builder.Register(
                    c =>
                    {
                        RetryStrategy defaultRetryStrategy = new FixedInterval(0, TimeSpan.FromSeconds(1));
                        TimeSpan defaultRevivePeriod = TimeSpan.FromHours(1);
                        TimeSpan defaultTimeout = TimeSpan.FromSeconds(60);
                        return new EndpointExecutorConfig(defaultTimeout, defaultRetryStrategy, defaultRevivePeriod, true);
                    })
                .As<EndpointExecutorConfig>()
                .SingleInstance();

            // IEndpointExecutorFactory
            builder.Register(c => new SyncEndpointExecutorFactory(c.Resolve<EndpointExecutorConfig>()))
                .As<IEndpointExecutorFactory>()
                .SingleInstance();

            // RouterConfig
            builder.Register(
                    c =>
                    {
                        Route route = c.Resolve<IRouteFactory>().Create(string.Empty);
                        return new RouterConfig(route.Endpoints, new[] { route });
                    })
                .As<RouterConfig>()
                .SingleInstance();

            // Task<Router>
            builder.Register(c => Router.CreateAsync(Guid.NewGuid().ToString(), this.iotHubName, c.Resolve<RouterConfig>(), c.Resolve<IEndpointExecutorFactory>()))
                .As<Task<Router>>()
                .SingleInstance();

            // Task<IEdgeHub>
            builder.Register(
                    async c =>
                    {
                        Router router = await c.Resolve<Task<Router>>();
                        IEdgeHub hub = new RoutingEdgeHub(router, c.Resolve<Core.IMessageConverter<IRoutingMessage>>());
                        return hub;
                    })
                .As<Task<IEdgeHub>>()
                .SingleInstance();

            base.Load(builder);
        }
    }
}