// Copyright (c) Microsoft. All rights reserved.

namespace Microsoft.Azure.Devices.Edge.Hub.Service.Modules
{
    using System;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading.Tasks;
    using Autofac;
    using Microsoft.Azure.Devices.Edge.Hub.CloudProxy;
    using Microsoft.Azure.Devices.Edge.Hub.CloudProxy.Authenticators;
    using Microsoft.Azure.Devices.Edge.Hub.Core;
    using Microsoft.Azure.Devices.Edge.Hub.Core.Identity;
    using Microsoft.Azure.Devices.Edge.Storage;
    using Microsoft.Azure.Devices.Edge.Util;
    using Microsoft.Azure.Devices.Edge.Util.Edged;
    using Microsoft.Extensions.Logging;

    public class CommonModule : Module
    {
        readonly string productInfo;
        readonly string iothubHostName;
        readonly string edgeDeviceId;
        readonly string edgeHubModuleId;
        readonly string edgeDeviceHostName;
        readonly Option<string> edgeHubGenerationId;
        readonly AuthenticationMode authenticationMode;
        readonly Option<string> edgeHubConnectionString;
        readonly bool optimizeForPerformance;
        readonly bool usePersistentStorage;
        readonly string storagePath;
        readonly TimeSpan scopeCacheRefreshRate;
        readonly Option<string> workloadUri;
        readonly bool persistTokens;

        public CommonModule(
            string productInfo,
            string iothubHostName,
            string edgeDeviceId,
            string edgeHubModuleId,
            string edgeDeviceHostName,
            Option<string> edgeHubGenerationId,
            AuthenticationMode authenticationMode,
            Option<string> edgeHubConnectionString,
            bool optimizeForPerformance,
            bool usePersistentStorage,
            string storagePath,
            Option<string> workloadUri,
            TimeSpan scopeCacheRefreshRate,
            bool persistTokens)
        {
            this.productInfo = productInfo;
            this.iothubHostName = Preconditions.CheckNonWhiteSpace(iothubHostName, nameof(iothubHostName));
            this.edgeDeviceId = Preconditions.CheckNonWhiteSpace(edgeDeviceId, nameof(edgeDeviceId));
            this.edgeHubModuleId = Preconditions.CheckNonWhiteSpace(edgeHubModuleId, nameof(edgeHubModuleId));
            this.edgeDeviceHostName = Preconditions.CheckNotNull(edgeDeviceHostName, nameof(edgeDeviceHostName));
            this.edgeHubGenerationId = edgeHubGenerationId;
            this.authenticationMode = authenticationMode;
            this.edgeHubConnectionString = edgeHubConnectionString;
            this.optimizeForPerformance = optimizeForPerformance;
            this.usePersistentStorage = usePersistentStorage;
            this.storagePath = storagePath;
            this.scopeCacheRefreshRate = scopeCacheRefreshRate;
            this.workloadUri = workloadUri;
            this.persistTokens = persistTokens;
        }

        protected override void Load(ContainerBuilder builder)
        {
            // ISignatureProvider
            builder.Register(
                    c =>
                    {
                        ISignatureProvider signatureProvider = this.edgeHubConnectionString.Map(
                                cs =>
                                {
                                    IotHubConnectionStringBuilder csBuilder = IotHubConnectionStringBuilder.Create(cs);
                                    return new SharedAccessKeySignatureProvider(csBuilder.SharedAccessKey) as ISignatureProvider;
                                })
                            .GetOrElse(
                                () =>
                                {
                                    string edgeHubGenerationId = this.edgeHubGenerationId.Expect(() => new InvalidOperationException("Generation ID missing"));
                                    string workloadUri = this.workloadUri.Expect(() => new InvalidOperationException("workloadUri is missing"));
                                    return new HttpHsmSignatureProvider(this.edgeHubModuleId, edgeHubGenerationId, workloadUri, Service.Constants.WorkloadApiVersion) as ISignatureProvider;
                                });
                        return signatureProvider;
                    })
                .As<ISignatureProvider>()
                .SingleInstance();

            // Detect system environment
            builder.Register(c => new SystemEnvironment())
                .As<ISystemEnvironment>()
                .SingleInstance();

            // DataBase options
            builder.Register(c => new Storage.RocksDb.RocksDbOptionsProvider(c.Resolve<ISystemEnvironment>(), this.optimizeForPerformance))
                .As<Storage.RocksDb.IRocksDbOptionsProvider>()
                .SingleInstance();

            // IDbStoreProvider
            builder.Register(
                c =>
                {
                    var loggerFactory = c.Resolve<ILoggerFactory>();
                    ILogger logger = loggerFactory.CreateLogger(typeof(RoutingModule));

                    if (this.usePersistentStorage)
                    {
                        // Create partitions for messages and twins
                        var partitionsList = new List<string> { Constants.MessageStorePartitionKey, Constants.TwinStorePartitionKey, Core.Constants.CheckpointStorePartitionKey };
                        try
                        {
                            IDbStoreProvider dbStoreprovider = Storage.RocksDb.DbStoreProvider.Create(c.Resolve<Storage.RocksDb.IRocksDbOptionsProvider>(),
                                this.storagePath, partitionsList);
                            logger.LogInformation($"Created persistent store at {this.storagePath}");
                            return dbStoreprovider;
                        }
                        catch (Exception ex) when (!ExceptionEx.IsFatal(ex))
                        {
                            logger.LogError(ex, "Error creating RocksDB store. Falling back to in-memory store.");
                            return new InMemoryDbStoreProvider();
                        }
                    }
                    else
                    {
                        logger.LogInformation($"Using in-memory store");
                        return new InMemoryDbStoreProvider();
                    }
                })
                .As<IDbStoreProvider>()
                .SingleInstance();

            // Task<Option<IEncryptionProvider>>
            builder.Register(
                    async c =>
                    {
                        Option<IEncryptionProvider> encryptionProviderOption = await this.workloadUri
                            .Map(
                                async uri =>
                                {
                                    var encryptionProvider = await EncryptionProvider.CreateAsync(
                                        this.storagePath,
                                        new Uri(uri),
                                        Service.Constants.WorkloadApiVersion,
                                        this.edgeHubModuleId,
                                        this.edgeHubGenerationId.Expect(() => new InvalidOperationException("Missing generation ID")),
                                        Service.Constants.InitializationVectorFileName) as IEncryptionProvider;
                                    return Option.Some(encryptionProvider);
                                })
                            .GetOrElse(() => Task.FromResult(Option.None<IEncryptionProvider>()));
                        return encryptionProviderOption;
                    })
                .As<Task<Option<IEncryptionProvider>>>()
                .SingleInstance();

            // IStoreProvider
            builder.Register(c => new StoreProvider(c.Resolve<IDbStoreProvider>()))
                .As<IStoreProvider>()
                .SingleInstance();

            // ITokenProvider
            builder.Register(c => new ModuleTokenProvider(c.Resolve<ISignatureProvider>(), this.iothubHostName, this.edgeDeviceId, this.edgeHubModuleId, TimeSpan.FromHours(1)))
                .Named<ITokenProvider>("EdgeHubClientAuthTokenProvider")
                .SingleInstance();

            // ITokenProvider
            builder.Register(c =>
                {
                    string deviceId = WebUtility.UrlEncode(this.edgeDeviceId);
                    string moduleId = WebUtility.UrlEncode(this.edgeHubModuleId);
                    return new ModuleTokenProvider(c.Resolve<ISignatureProvider>(), this.iothubHostName, deviceId, moduleId, TimeSpan.FromHours(1));
                })
                .Named<ITokenProvider>("EdgeHubServiceAuthTokenProvider")
                .SingleInstance();

            // Task<IDeviceScopeIdentitiesCache>
            builder.Register(
                    async c =>
                    {
                        IDeviceScopeIdentitiesCache deviceScopeIdentitiesCache;
                        if (this.authenticationMode == AuthenticationMode.CloudAndScope || this.authenticationMode == AuthenticationMode.Scope)
                        {
                            var edgeHubTokenProvider = c.ResolveNamed<ITokenProvider>("EdgeHubServiceAuthTokenProvider");
                            IDeviceScopeApiClient securityScopesApiClient = new DeviceScopeApiClient(this.iothubHostName, this.edgeDeviceId, this.edgeHubModuleId, 10, edgeHubTokenProvider);
                            IServiceProxy serviceProxy = new ServiceProxy(securityScopesApiClient);
                            IKeyValueStore<string, string> encryptedStore = await GetEncryptedStore(c, "DeviceScopeCache");
                            deviceScopeIdentitiesCache = await DeviceScopeIdentitiesCache.Create(serviceProxy, encryptedStore, this.scopeCacheRefreshRate);
                        }
                        else
                        {
                            deviceScopeIdentitiesCache = new NullDeviceScopeIdentitiesCache();
                        }

                        return deviceScopeIdentitiesCache;
                    })
                .As<Task<IDeviceScopeIdentitiesCache>>()
                .AutoActivate()
                .SingleInstance();

            // Task<ICredentialsCache>
            builder.Register(async c =>
                {
                    ICredentialsCache underlyingCredentialsCache;
                    if (this.persistTokens)
                    {
                        IKeyValueStore<string, string> encryptedStore = await GetEncryptedStore(c, "CredentialsCache");
                        return new TokenCredentialsCache(encryptedStore);
                    }
                    else
                    {
                        underlyingCredentialsCache = new NullCredentialsCache();
                    }
                    ICredentialsCache credentialsCache = new CredentialsCache(underlyingCredentialsCache);
                    return credentialsCache;
                })
                .As<Task<ICredentialsCache>>()
                .SingleInstance();

            // Task<IAuthenticator>
            builder.Register(async c =>
                {                    
                    IAuthenticator tokenAuthenticator;
                    IDeviceScopeIdentitiesCache deviceScopeIdentitiesCache;
                    var credentialsCacheTask = c.Resolve<Task<ICredentialsCache>>();
                    switch (this.authenticationMode)
                    {
                        case AuthenticationMode.Cloud:
                            tokenAuthenticator = await this.GetCloudTokenAuthenticator(c);
                            break;

                        case AuthenticationMode.Scope:
                            deviceScopeIdentitiesCache = await c.Resolve<Task<IDeviceScopeIdentitiesCache>>();
                            tokenAuthenticator = new DeviceScopeTokenAuthenticator(deviceScopeIdentitiesCache, this.iothubHostName, this.edgeDeviceHostName, new NullAuthenticator());
                            break;

                        default:                            
                            var deviceScopeIdentitiesCacheTask = c.Resolve<Task<IDeviceScopeIdentitiesCache>>();
                            IAuthenticator cloudTokenAuthenticator = await this.GetCloudTokenAuthenticator(c);
                            deviceScopeIdentitiesCache = await deviceScopeIdentitiesCacheTask;
                            tokenAuthenticator = new DeviceScopeTokenAuthenticator(deviceScopeIdentitiesCache, this.iothubHostName, this.edgeDeviceHostName, cloudTokenAuthenticator);
                            break;
                    }

                    ICredentialsCache credentialsCache = await credentialsCacheTask;
                    return new Authenticator(tokenAuthenticator, this.edgeDeviceId, credentialsCache) as IAuthenticator;
                })
                .As<Task<IAuthenticator>>()
                .SingleInstance();

            // IClientCredentialsFactory
            builder.Register(c => new ClientCredentialsFactory(this.iothubHostName, this.productInfo))
                .As<IClientCredentialsFactory>()
                .SingleInstance();

            // ConnectionReauthenticator
            builder.Register(async c =>
                {
                    var edgeHubCredentials = c.ResolveNamed<IClientCredentials>("EdgeHubCredentials");
                    var connectionManagerTask = c.Resolve<Task<IConnectionManager>>();
                    var authenticatorTask = c.Resolve<Task<IAuthenticator>>();
                    var credentialsCacheTask = c.Resolve<Task<ICredentialsCache>>();
                    var deviceScopeIdentitiesCacheTask = c.Resolve<Task<IDeviceScopeIdentitiesCache>>();
                    IConnectionManager connectionManager = await connectionManagerTask;
                    IAuthenticator authenticator = await authenticatorTask;
                    ICredentialsCache credentialsCache = await credentialsCacheTask;
                    IDeviceScopeIdentitiesCache deviceScopeIdentitiesCache = await deviceScopeIdentitiesCacheTask;
                    var connectionReauthenticator = new ConnectionReauthenticator(
                        connectionManager,
                        authenticator,
                        credentialsCache,
                        deviceScopeIdentitiesCache,
                        TimeSpan.FromMinutes(5),
                        edgeHubCredentials.Identity); 
                    return connectionReauthenticator;
                })
                .As<Task<ConnectionReauthenticator>>()
                .SingleInstance();

            base.Load(builder);
        }

        async Task<IAuthenticator> GetCloudTokenAuthenticator(IComponentContext context)
        {
            IAuthenticator tokenAuthenticator;
            var connectionManagerTask = context.Resolve<Task<IConnectionManager>>();
            var credentialsCacheTask = context.Resolve<Task<ICredentialsCache>>();
            IConnectionManager connectionManager = await connectionManagerTask;
            ICredentialsCache credentialsCache = await credentialsCacheTask;
            if (this.persistTokens)
            {
                IAuthenticator authenticator = new CloudTokenAuthenticator(connectionManager, this.iothubHostName);
                tokenAuthenticator = new TokenCacheAuthenticator(authenticator, credentialsCache, this.iothubHostName);
            }
            else
            {
                tokenAuthenticator = new CloudTokenAuthenticator(connectionManager, this.iothubHostName);
            }

            return tokenAuthenticator;
        }

        static async Task<IKeyValueStore<string, string>> GetEncryptedStore(IComponentContext context, string entityName)
        {
            var storeProvider = context.Resolve<IStoreProvider>();
            Option<IEncryptionProvider> encryptionProvider = await context.Resolve<Task<Option<IEncryptionProvider>>>();
            IKeyValueStore<string, string> encryptedStore = encryptionProvider
                .Map(
                    e =>
                    {
                        IEntityStore<string, string> entityStore = storeProvider.GetEntityStore<string, string>(entityName);
                        IKeyValueStore<string, string> es = new EncryptedStore<string, string>(entityStore, e);
                        return es;
                    })
                .GetOrElse(() => new NullKeyValueStore<string, string>() as IKeyValueStore<string, string>);
            return encryptedStore;
        }
    }
}
