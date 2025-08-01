﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Net;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Cosmos.Routing;
    using Microsoft.Azure.Documents;

    /// <summary>
    /// Client policy is combination of endpoint change retry + throttling retry.
    /// </summary>
    internal sealed class ClientRetryPolicy : IDocumentClientRetryPolicy
    {
        private const int RetryIntervalInMS = 1000; // Once we detect failover wait for 1 second before retrying request.
        private const int MaxRetryCount = 120;
        private const int MaxServiceUnavailableRetryCount = 1;

        private readonly IDocumentClientRetryPolicy throttlingRetry;
        private readonly GlobalEndpointManager globalEndpointManager;
        private readonly GlobalPartitionEndpointManager partitionKeyRangeLocationCache;
        private readonly bool enableEndpointDiscovery;
        private readonly bool isPartitionLevelFailoverEnabled;
        private readonly bool isThinClientEnabled;
        private int failoverRetryCount;

        private int sessionTokenRetryCount;
        private int serviceUnavailableRetryCount;
        private bool isReadRequest;
        private bool canUseMultipleWriteLocations;
        private bool isMultiMasterWriteRequest;
        private Uri locationEndpoint;
        private RetryContext retryContext;
        private DocumentServiceRequest documentServiceRequest;

        public ClientRetryPolicy(
            GlobalEndpointManager globalEndpointManager,
            GlobalPartitionEndpointManager partitionKeyRangeLocationCache,
            RetryOptions retryOptions,
            bool enableEndpointDiscovery,
            bool isPartitionLevelFailoverEnabled,
            bool isThinClientEnabled)
        {
            this.throttlingRetry = new ResourceThrottleRetryPolicy(
                retryOptions.MaxRetryAttemptsOnThrottledRequests,
                retryOptions.MaxRetryWaitTimeInSeconds);

            this.globalEndpointManager = globalEndpointManager;
            this.partitionKeyRangeLocationCache = partitionKeyRangeLocationCache;
            this.failoverRetryCount = 0;
            this.enableEndpointDiscovery = enableEndpointDiscovery;
            this.sessionTokenRetryCount = 0;
            this.serviceUnavailableRetryCount = 0;
            this.canUseMultipleWriteLocations = false;
            this.isMultiMasterWriteRequest = false;
            this.isPartitionLevelFailoverEnabled = isPartitionLevelFailoverEnabled;
            this.isThinClientEnabled = isThinClientEnabled;
        }

        /// <summary> 
        /// Should the caller retry the operation.
        /// </summary>
        /// <param name="exception">Exception that occurred when the operation was tried</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True indicates caller should retry, False otherwise</returns>
        public async Task<ShouldRetryResult> ShouldRetryAsync(
            Exception exception,
            CancellationToken cancellationToken)
        {
            this.retryContext = null;
            // Received Connection error (HttpRequestException), initiate the endpoint rediscovery
            if (exception is HttpRequestException _)
            {
                DefaultTrace.TraceWarning("ClientRetryPolicy: Gateway HttpRequestException Endpoint not reachable. Failed Location: {0}; ResourceAddress: {1}",
                    this.documentServiceRequest?.RequestContext?.LocationEndpointToRoute?.ToString() ?? string.Empty,
                    this.documentServiceRequest?.ResourceAddress ?? string.Empty);

                // In the event of the routing gateway having outage on region A, mark the partition as unavailable assuming that the
                // partition has been failed over to region B, when per partition automatic failover is enabled.
                this.TryMarkEndpointUnavailableForPkRange(isSystemResourceUnavailableForWrite: false);

                // Mark both read and write requests because it gateway exception.
                // This means all requests going to the region will fail.
                return await this.ShouldRetryOnEndpointFailureAsync(
                    isReadRequest: this.isReadRequest,
                    markBothReadAndWriteAsUnavailable: true,
                    forceRefresh: false,
                    retryOnPreferredLocations: true);
            }

            if (exception is DocumentClientException clientException)
            {
                // Today, the only scenario where we would treat a throttling (429) exception as service unavailable is when we
                // get 429 (TooManyRequests) with sub status code 3092 (System Resource Not Available). Note that this is applicable
                // for write requests targeted to a multiple master account. In such case, the 429/3092 will be treated as 503. The
                // reason to keep the code out of the throttling retry policy is that in the near future, the 3092 sub status code
                // might not be a throttling scenario at all and the status code in that case would be different than 429.
                if (this.ShouldMarkEndpointUnavailableOnSystemResourceUnavailableForWrite(
                    clientException.StatusCode,
                    clientException.GetSubStatus()))
                {
                    DefaultTrace.TraceError(
                        "Operation will NOT be retried on local region. Treating SystemResourceUnavailable (429/3092) as ServiceUnavailable (503). Status code: {0}, sub status code: {1}.",
                        StatusCodes.TooManyRequests, SubStatusCodes.SystemResourceUnavailable);

                    return this.TryMarkEndpointUnavailableForPkRangeAndRetryOnServiceUnavailable(
                        isSystemResourceUnavailableForWrite: true);
                }

                ShouldRetryResult shouldRetryResult = await this.ShouldRetryInternalAsync(
                    clientException?.StatusCode,
                    clientException?.GetSubStatus());
                if (shouldRetryResult != null)
                {
                    return shouldRetryResult;
                }
            }

            // Any metadata request will throw a cosmos exception from CosmosHttpClientCore if
            // it receives a 503 service unavailable from gateway. This check is to add retry
            // mechanism for the metadata requests in such cases.
            if (exception is CosmosException cosmosException)
            {
                ShouldRetryResult shouldRetryResult = await this.ShouldRetryInternalAsync(
                    cosmosException.StatusCode,
                    cosmosException.Headers.SubStatusCode);
                if (shouldRetryResult != null)
                {
                    return shouldRetryResult;
                }
            }

            if (exception is OperationCanceledException)
            {
                DefaultTrace.TraceInformation("ClientRetryPolicy: The operation was cancelled. Not retrying. Retry count = {0}, Endpoint = {1}",
                    this.failoverRetryCount,
                    this.locationEndpoint?.ToString() ?? string.Empty);

                if (this.partitionKeyRangeLocationCache.IncrementRequestFailureCounterAndCheckIfPartitionCanFailover(
                        this.documentServiceRequest))
                {
                    // In the event of a (ppaf + write operation) or (ppcb + read or multi-master write operation) getting timed
                    // out due to cancellation token expiration on region A, mark the partition as unavailable assuming that
                    // the partition has been failed over to region B, when per partition automatic failover is enabled.
                    this.partitionKeyRangeLocationCache.TryMarkEndpointUnavailableForPartitionKeyRange(
                         this.documentServiceRequest);
                }
            }

            return await this.throttlingRetry.ShouldRetryAsync(exception, cancellationToken);
        }

        /// <summary> 
        /// Should the caller retry the operation.
        /// </summary>
        /// <param name="cosmosResponseMessage"><see cref="ResponseMessage"/> in return of the request</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True indicates caller should retry, False otherwise</returns>
        public async Task<ShouldRetryResult> ShouldRetryAsync(
            ResponseMessage cosmosResponseMessage,
            CancellationToken cancellationToken)
        {
            this.retryContext = null;

            ShouldRetryResult shouldRetryResult = await this.ShouldRetryInternalAsync(
                    cosmosResponseMessage?.StatusCode,
                    cosmosResponseMessage?.Headers.SubStatusCode);
            if (shouldRetryResult != null)
            {
                return shouldRetryResult;
            }

            // Today, the only scenario where we would treat a throttling (429) exception as service unavailable is when we
            // get 429 (TooManyRequests) with sub status code 3092 (System Resource Not Available). Note that this is applicable
            // for write requests targeted to a multiple master account. In such case, the 429/3092 will be treated as 503. The
            // reason to keep the code out of the throttling retry policy is that in the near future, the 3092 sub status code
            // might not be a throttling scenario at all and the status code in that case would be different than 429.
            if (this.ShouldMarkEndpointUnavailableOnSystemResourceUnavailableForWrite(
                cosmosResponseMessage.StatusCode,
                cosmosResponseMessage?.Headers.SubStatusCode))
            {
                DefaultTrace.TraceError(
                    "Operation will NOT be retried on local region. Treating SystemResourceUnavailable (429/3092) as ServiceUnavailable (503). Status code: {0}, sub status code: {1}.",
                    StatusCodes.TooManyRequests, SubStatusCodes.SystemResourceUnavailable);

                return this.TryMarkEndpointUnavailableForPkRangeAndRetryOnServiceUnavailable(
                    isSystemResourceUnavailableForWrite: true);
            }

            return await this.throttlingRetry.ShouldRetryAsync(cosmosResponseMessage, cancellationToken);
        }

        /// <summary>
        /// Method that is called before a request is sent to allow the retry policy implementation
        /// to modify the state of the request.
        /// </summary>
        /// <param name="request">The request being sent to the service.</param>
        public void OnBeforeSendRequest(DocumentServiceRequest request)
        {
            this.isReadRequest = request.IsReadOnlyRequest;
            this.canUseMultipleWriteLocations = this.globalEndpointManager.CanUseMultipleWriteLocations(request);
            this.documentServiceRequest = request;
            this.isMultiMasterWriteRequest = !this.isReadRequest
                && (this.globalEndpointManager?.CanSupportMultipleWriteLocations(request.ResourceType, request.OperationType) ?? false);

            // clear previous location-based routing directive
            request.RequestContext.ClearRouteToLocation();

            if (this.retryContext != null)
            {
                if (this.retryContext.RouteToHub)
                {
                    request.RequestContext.RouteToLocation(this.globalEndpointManager.GetHubUri());
                }
                else
                {
                    // set location-based routing directive based on request retry context
                    request.RequestContext.RouteToLocation(this.retryContext.RetryLocationIndex, this.retryContext.RetryRequestOnPreferredLocations);
                }
            }

            // Resolve the endpoint for the request and pin the resolution to the resolved endpoint
            // This enables marking the endpoint unavailability on endpoint failover/unreachability
            this.locationEndpoint = this.isThinClientEnabled
                && ThinClientStoreModel.IsOperationSupportedByThinClient(request)
                ? this.globalEndpointManager.ResolveThinClientEndpoint(request)
                : this.globalEndpointManager.ResolveServiceEndpoint(request);

            request.RequestContext.RouteToLocation(this.locationEndpoint);
        }

        private async Task<ShouldRetryResult> ShouldRetryInternalAsync(
            HttpStatusCode? statusCode,
            SubStatusCodes? subStatusCode)
        {
            if (!statusCode.HasValue
                && (!subStatusCode.HasValue
                || subStatusCode.Value == SubStatusCodes.Unknown))
            {
                return null;
            }

            // Received request timeout
            if (statusCode == HttpStatusCode.RequestTimeout)
            {
                DefaultTrace.TraceWarning("ClientRetryPolicy: RequestTimeout. Failed Location: {0}; ResourceAddress: {1}",
                    this.documentServiceRequest?.RequestContext?.LocationEndpointToRoute?.ToString() ?? string.Empty,
                    this.documentServiceRequest?.ResourceAddress ?? string.Empty);

                // Mark the partition key range as unavailable to retry future request on a new region.
                this.TryMarkEndpointUnavailableForPkRange(isSystemResourceUnavailableForWrite: false);
            }

            // Received 403.3 on write region, initiate the endpoint rediscovery
            if (statusCode == HttpStatusCode.Forbidden
                && subStatusCode == SubStatusCodes.WriteForbidden)
            {
                // It's a write forbidden so it safe to retry
                if (this.partitionKeyRangeLocationCache.TryMarkEndpointUnavailableForPartitionKeyRange(
                     this.documentServiceRequest))
                {
                    return ShouldRetryResult.RetryAfter(TimeSpan.Zero);
                }

                DefaultTrace.TraceWarning("ClientRetryPolicy: Endpoint not writable. Refresh cache and retry. Failed Location: {0}; ResourceAddress: {1}",
                    this.documentServiceRequest?.RequestContext?.LocationEndpointToRoute?.ToString() ?? string.Empty,
                    this.documentServiceRequest?.ResourceAddress ?? string.Empty);

                if (this.globalEndpointManager.IsMultimasterMetadataWriteRequest(this.documentServiceRequest))
                {
                    bool forceRefresh = false;

                    if (this.retryContext != null && this.retryContext.RouteToHub)
                    {
                        forceRefresh = true;
                        
                    }

                    ShouldRetryResult retryResult = await this.ShouldRetryOnEndpointFailureAsync(
                        isReadRequest: false,
                        markBothReadAndWriteAsUnavailable: false,
                        forceRefresh: forceRefresh,
                        retryOnPreferredLocations: false,
                        overwriteEndpointDiscovery: true);

                    if (retryResult.ShouldRetry)
                    {
                        this.retryContext.RouteToHub = true;
                    }
                    
                    return retryResult;
                }

                return await this.ShouldRetryOnEndpointFailureAsync(
                    isReadRequest: false,
                    markBothReadAndWriteAsUnavailable: false,
                    forceRefresh: true,
                    retryOnPreferredLocations: false);
            }

            // Regional endpoint is not available yet for reads (e.g. add/ online of region is in progress)
            if (statusCode == HttpStatusCode.Forbidden
                && subStatusCode == SubStatusCodes.DatabaseAccountNotFound
                && (this.isReadRequest || this.canUseMultipleWriteLocations))
            {
                DefaultTrace.TraceWarning("ClientRetryPolicy: Endpoint not available for reads. Refresh cache and retry. Failed Location: {0}; ResourceAddress: {1}",
                    this.documentServiceRequest?.RequestContext?.LocationEndpointToRoute?.ToString() ?? string.Empty,
                    this.documentServiceRequest?.ResourceAddress ?? string.Empty);

                //Retry policy will retry on the next preffered region as the original requert region is not accepting requests
                return await this.ShouldRetryOnEndpointFailureAsync(
                    isReadRequest: this.isReadRequest,
                    markBothReadAndWriteAsUnavailable: false,
                    forceRefresh: false,
                    retryOnPreferredLocations: true);
            }

            if (statusCode == HttpStatusCode.NotFound
                && subStatusCode == SubStatusCodes.ReadSessionNotAvailable)
            {
                return this.ShouldRetryOnSessionNotAvailable(this.documentServiceRequest);
            }
            
            // Received 503 due to client connect timeout or Gateway
            if (statusCode == HttpStatusCode.ServiceUnavailable)
            {
                return this.TryMarkEndpointUnavailableForPkRangeAndRetryOnServiceUnavailable(
                    isSystemResourceUnavailableForWrite: false);
            }

            // Recieved 500 status code or lease not found
            if ((statusCode == HttpStatusCode.InternalServerError && this.isReadRequest)
                || (statusCode == HttpStatusCode.Gone && subStatusCode == SubStatusCodes.LeaseNotFound))
            {
                return this.ShouldRetryOnUnavailableEndpointStatusCodes();
            }

            return null;
        }

        private async Task<ShouldRetryResult> ShouldRetryOnEndpointFailureAsync(
            bool isReadRequest,
            bool markBothReadAndWriteAsUnavailable,
            bool forceRefresh,
            bool retryOnPreferredLocations,
            bool overwriteEndpointDiscovery = false)
        {
            if (this.failoverRetryCount > MaxRetryCount || (!this.enableEndpointDiscovery && !overwriteEndpointDiscovery))
            {
                DefaultTrace.TraceInformation("ClientRetryPolicy: ShouldRetryOnEndpointFailureAsync() Not retrying. Retry count = {0}, Endpoint = {1}", 
                    this.failoverRetryCount,
                    this.locationEndpoint?.ToString() ?? string.Empty);
                return ShouldRetryResult.NoRetry();
            }

            this.failoverRetryCount++;

            if (this.locationEndpoint != null && !overwriteEndpointDiscovery)
            {
                if (isReadRequest || markBothReadAndWriteAsUnavailable)
                {
                    this.globalEndpointManager.MarkEndpointUnavailableForRead(this.locationEndpoint);
                }
                
                if (!isReadRequest || markBothReadAndWriteAsUnavailable)
                {
                    this.globalEndpointManager.MarkEndpointUnavailableForWrite(this.locationEndpoint);
                }
            }

            TimeSpan retryDelay = TimeSpan.Zero;
            if (!isReadRequest)
            {
                DefaultTrace.TraceInformation("ClientRetryPolicy: Failover happening. retryCount {0}", this.failoverRetryCount);

                if (this.failoverRetryCount > 1)
                {
                    //if retried both endpoints, follow regular retry interval.
                    retryDelay = TimeSpan.FromMilliseconds(ClientRetryPolicy.RetryIntervalInMS);
                }
            }
            else
            {
                retryDelay = TimeSpan.FromMilliseconds(ClientRetryPolicy.RetryIntervalInMS);
            }

            await this.globalEndpointManager.RefreshLocationAsync(forceRefresh);

            int retryLocationIndex = this.failoverRetryCount; // Used to generate a round-robin effect
            if (retryOnPreferredLocations)
            {
                retryLocationIndex = 0; // When the endpoint is marked as unavailable, it is moved to the bottom of the preferrence list
            }

            this.retryContext = new RetryContext
            {
                RetryLocationIndex = retryLocationIndex,
                RetryRequestOnPreferredLocations = retryOnPreferredLocations,
            };

            return ShouldRetryResult.RetryAfter(retryDelay);
        }

        private ShouldRetryResult ShouldRetryOnSessionNotAvailable(DocumentServiceRequest request)
        {
            this.sessionTokenRetryCount++;

            if (!this.enableEndpointDiscovery)
            {
                // if endpoint discovery is disabled, the request cannot be retried anywhere else
                return ShouldRetryResult.NoRetry();
            }
            else
            {
                if (this.canUseMultipleWriteLocations)
                {
                    ReadOnlyCollection<Uri> endpoints = this.globalEndpointManager.GetApplicableEndpoints(request, this.isReadRequest);

                    if (this.sessionTokenRetryCount > endpoints.Count)
                    {
                        // When use multiple write locations is true and the request has been tried 
                        // on all locations, then don't retry the request
                        return ShouldRetryResult.NoRetry();
                    }
                    else
                    {
                        this.retryContext = new RetryContext()
                        {
                            RetryLocationIndex = this.sessionTokenRetryCount,
                            RetryRequestOnPreferredLocations = true
                        };

                        return ShouldRetryResult.RetryAfter(TimeSpan.Zero);
                    }
                }
                else
                {
                    if (this.sessionTokenRetryCount > 1)
                    {
                        // When cannot use multiple write locations, then don't retry the request if 
                        // we have already tried this request on the write location
                        return ShouldRetryResult.NoRetry();
                    }
                    else
                    {
                        this.retryContext = new RetryContext
                        {
                            RetryLocationIndex = 0,
                            RetryRequestOnPreferredLocations = false
                        };

                        return ShouldRetryResult.RetryAfter(TimeSpan.Zero);
                    }
                }
            }
        }

        /// <summary>
        /// Attempts to mark the endpoint associated with the current partition key range as unavailable and determines if
        /// a retry should be performed due to a ServiceUnavailable (503) response. This method is invoked when a 503
        /// Service Unavailable response is received, indicating that the service might be temporarily unavailable.
        /// It optionally marks the partition key range as unavailable, which will influence future routing decisions.
        /// </summary>
        /// <param name="isSystemResourceUnavailableForWrite">A boolean flag indicating whether the endpoint for the
        /// current partition key range should be marked as unavailable, if the failure happened due to system
        /// resource unavailability.</param>
        /// <returns>An instance of <see cref="ShouldRetryResult"/> indicating whether the operation should be retried.</returns>
        private ShouldRetryResult TryMarkEndpointUnavailableForPkRangeAndRetryOnServiceUnavailable(
            bool isSystemResourceUnavailableForWrite)
        {
            DefaultTrace.TraceWarning("ClientRetryPolicy: ServiceUnavailable. Refresh cache and retry. Failed Location: {0}; ResourceAddress: {1}",
                this.documentServiceRequest?.RequestContext?.LocationEndpointToRoute?.ToString() ?? string.Empty,
                this.documentServiceRequest?.ResourceAddress ?? string.Empty);

            this.TryMarkEndpointUnavailableForPkRange(isSystemResourceUnavailableForWrite);

            return this.ShouldRetryOnUnavailableEndpointStatusCodes();
        }

        /// <summary>
        /// For a ServiceUnavailable (503.0) we could be having a timeout from Direct/TCP locally or a request to Gateway request with a similar response due to an endpoint not yet available.
        /// We try and retry the request only if there are other regions available. The retry logic is applicable for single master write accounts as well.
        /// Other status codes include InternalServerError (500.0) and LeaseNotFound (410.1022).
        /// </summary>
        private ShouldRetryResult ShouldRetryOnUnavailableEndpointStatusCodes()
        {
            if (this.serviceUnavailableRetryCount++ >= ClientRetryPolicy.MaxServiceUnavailableRetryCount)
            {
                DefaultTrace.TraceInformation($"ClientRetryPolicy: ShouldRetryOnServiceUnavailable() Not retrying. Retry count = {this.serviceUnavailableRetryCount}.");
                return ShouldRetryResult.NoRetry();
            }

            if (!this.canUseMultipleWriteLocations
                    && !this.isReadRequest
                    && !this.isPartitionLevelFailoverEnabled)
            {
                // Write requests on single master cannot be retried if partition level failover is disabled.
                // This means there are no other regions available to serve the writes.
                return ShouldRetryResult.NoRetry();
            }

            int availablePreferredLocations = this.globalEndpointManager.PreferredLocationCount;

            if (availablePreferredLocations <= 1)
            {
                // No other regions to retry on
                DefaultTrace.TraceInformation($"ClientRetryPolicy: ShouldRetryOnServiceUnavailable() Not retrying. No other regions available for the request. AvailablePreferredLocations = {availablePreferredLocations}.");
                return ShouldRetryResult.NoRetry();
            }

            DefaultTrace.TraceInformation($"ClientRetryPolicy: ShouldRetryOnServiceUnavailable() Retrying. Received on endpoint {this.locationEndpoint}, IsReadRequest = {this.isReadRequest}.");

            // Retrying on second PreferredLocations
            // RetryCount is used as zero-based index
            this.retryContext = new RetryContext()
            {
                RetryLocationIndex = this.serviceUnavailableRetryCount,
                RetryRequestOnPreferredLocations = true
            };

            return ShouldRetryResult.RetryAfter(TimeSpan.Zero);
        }

        /// <summary>
        /// Attempts to mark the endpoint associated with the current partition key range as unavailable
        /// which will influence future routing decisions.
        /// </summary>
        /// <param name="isSystemResourceUnavailableForWrite">A boolean flag indicating if the system resource was unavailable. If true,
        /// the endpoint will be marked unavailable for the pk-range of a multi master write request, bypassing the circuit breaker check.</param>
        /// <returns>A boolean flag indicating whether the endpoint was marked as unavailable.</returns>
        private bool TryMarkEndpointUnavailableForPkRange(
            bool isSystemResourceUnavailableForWrite)
        {
            if (this.documentServiceRequest != null
                && (isSystemResourceUnavailableForWrite
                || this.IsRequestEligibleForPerPartitionAutomaticFailover()
                || this.IsRequestEligibleForPartitionLevelCircuitBreaker()))
            {
                // Mark the partition as unavailable.
                // Let the ClientRetry logic decide if the request should be retried
                return this.partitionKeyRangeLocationCache.TryMarkEndpointUnavailableForPartitionKeyRange(
                    request: this.documentServiceRequest);
            }

            return false;
        }

        /// <summary>
        /// Returns a boolean flag indicating if the endpoint should be marked as unavailable
        /// due to a 429 response with a sub status code of 3092 (system resource unavailable).
        /// This is applicable for write requests targeted for multi master accounts.
        /// </summary>
        /// <param name="statusCode">An instance of <see cref="HttpStatusCode"/> containing the status code.</param>
        /// <param name="subStatusCode">An instance of <see cref="SubStatusCodes"/> containing the sub status code.</param>
        /// <returns>A boolean flag indicating is the endpoint should be marked as unavailable.</returns>
        private bool ShouldMarkEndpointUnavailableOnSystemResourceUnavailableForWrite(
            HttpStatusCode? statusCode,
            SubStatusCodes? subStatusCode)
        {
            return this.isMultiMasterWriteRequest
                && statusCode.HasValue
                && (int)statusCode.Value == (int)StatusCodes.TooManyRequests
                && subStatusCode == SubStatusCodes.SystemResourceUnavailable;
        }

        /// <summary>
        /// Determines if a request is eligible for per-partition automatic failover.
        /// A request is eligible if it is a write request, partition level failover is enabled,
        /// and the global endpoint manager cannot use multiple write locations for the request.
        /// </summary>
        /// <returns>True if the request is eligible for per-partition automatic failover, otherwise false.</returns>
        private bool IsRequestEligibleForPerPartitionAutomaticFailover()
        {
            return this.partitionKeyRangeLocationCache.IsRequestEligibleForPerPartitionAutomaticFailover(
                this.documentServiceRequest);
        }

        /// <summary>
        /// Determines if a request is eligible for partition-level circuit breaker.
        /// This method checks if the request is a read-only request or a multi master write request, if partition-level circuit breaker is enabled,
        /// and if the partition key range location cache indicates that the partition can fail over based on the number of request failures.
        /// </summary>
        /// <returns>
        /// True if the read request is eligible for partition-level circuit breaker, otherwise false.
        /// </returns>
        private bool IsRequestEligibleForPartitionLevelCircuitBreaker()
        {
            return this.partitionKeyRangeLocationCache.IsRequestEligibleForPartitionLevelCircuitBreaker(this.documentServiceRequest)
                        && this.partitionKeyRangeLocationCache.IncrementRequestFailureCounterAndCheckIfPartitionCanFailover(this.documentServiceRequest);
        }

        private sealed class RetryContext
        {
            public int RetryLocationIndex { get; set; }
            public bool RetryRequestOnPreferredLocations { get; set; }

            public bool RouteToHub { get; set; }
        }
    }
}