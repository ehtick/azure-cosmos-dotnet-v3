﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos
{
    using Microsoft.Azure.Cosmos.Routing;

    /// <summary>
    /// Represents the retry policy configuration assocated with a DocumentClient instance.
    /// </summary>
    internal sealed class RetryPolicy : IRetryPolicyFactory
    {
        private readonly GlobalPartitionEndpointManager partitionKeyRangeLocationCache;
        private readonly GlobalEndpointManager globalEndpointManager;
        private readonly bool enableEndpointDiscovery;
        private readonly bool isPartitionLevelFailoverEnabled;
        private readonly bool isThinClientEnabled;
        private readonly RetryOptions retryOptions;

        /// <summary>
        /// Initialize the instance of the RetryPolicy class
        /// </summary>
        public RetryPolicy(
            GlobalEndpointManager globalEndpointManager,
            ConnectionPolicy connectionPolicy,
            GlobalPartitionEndpointManager partitionKeyRangeLocationCache,
            bool isThinClientEnabled)
        {
            this.enableEndpointDiscovery = connectionPolicy.EnableEndpointDiscovery;
            this.isPartitionLevelFailoverEnabled = connectionPolicy.EnablePartitionLevelFailover;
            this.globalEndpointManager = globalEndpointManager;
            this.retryOptions = connectionPolicy.RetryOptions;
            this.partitionKeyRangeLocationCache = partitionKeyRangeLocationCache;
            this.isThinClientEnabled = isThinClientEnabled;
        }

        /// <summary>
        /// Creates a new instance of the ClientRetryPolicy class retrying request failures.
        /// </summary>
        public IDocumentClientRetryPolicy GetRequestPolicy()
        {
            ClientRetryPolicy clientRetryPolicy = new ClientRetryPolicy(
                this.globalEndpointManager,
                this.partitionKeyRangeLocationCache,
                this.retryOptions,
                this.enableEndpointDiscovery,
                this.isPartitionLevelFailoverEnabled,
                this.isThinClientEnabled);

            return clientRetryPolicy;
        }
    }
}