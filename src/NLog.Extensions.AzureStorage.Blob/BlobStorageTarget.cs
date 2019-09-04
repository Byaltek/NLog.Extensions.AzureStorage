﻿using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Blob;
using NLog.Common;
using NLog.Config;
using NLog.Extensions.AzureStorage.Common;
using NLog.Layouts;
using NLog.Targets;

namespace NLog.Extensions.AzureStorage.Blob
{
    /// <summary>
    /// Azure Blob Storage NLog Target
    /// </summary>
    /// <seealso cref="NLog.Targets.TargetWithLayout" />
    [Target("AzureBlobStorage")]
    public sealed class BlobStorageTarget : TargetWithLayout
    {
        private CloudBlobClient _client;
        private CloudAppendBlob _appendBlob;
        private CloudBlobContainer _container;
        private readonly AzureStorageNameCache _containerNameCache = new AzureStorageNameCache();
        private readonly Func<string, string> _checkAndRepairContainerNameDelegate;
        private readonly char[] _reusableEncodingBuffer = new char[32 * 1024];  // Avoid large-object-heap
        private readonly StringBuilder _reusableEncodingBuilder = new StringBuilder(1024);

        //Delegates for bucket sorting
        private SortHelpers.KeySelector<AsyncLogEventInfo, ContainerBlobKey> _getContainerBlobNameDelegate;
        struct ContainerBlobKey : IEquatable<ContainerBlobKey>
        {
            public readonly string ContainerName;
            public readonly string BlobName;

            public ContainerBlobKey(string containerName, string blobName)
            {
                ContainerName = containerName;
                BlobName = blobName;
            }

            public bool Equals(ContainerBlobKey other)
            {
                return ContainerName == other.ContainerName &&
                       BlobName == other.BlobName;
            }

            public override bool Equals(object obj)
            {
                return (obj is ContainerBlobKey) && Equals((ContainerBlobKey)obj);
            }

            public override int GetHashCode()
            {
                return ContainerName.GetHashCode() ^ BlobName.GetHashCode();
            }
        }

        public string ConnectionString { get => (_connectionString as SimpleLayout)?.Text ?? null; set => _connectionString = value; }
        private Layout _connectionString;
        public string ConnectionStringKey { get; set; }

        [RequiredParameter]
        public Layout Container { get; set; }

        [RequiredParameter]
        public Layout BlobName { get; set; }

        public string ContentType { get; set; } = "text/plain";

        public BlobStorageTarget()
        {
            OptimizeBufferReuse = true;
            _checkAndRepairContainerNameDelegate = CheckAndRepairContainerNamingRules;
        }

        /// <summary>
        /// Initializes the target. Can be used by inheriting classes
        /// to initialize logging.
        /// </summary>
        protected override void InitializeTarget()
        {
            base.InitializeTarget();

            var connectionString = string.Empty;
            try
            {
                connectionString = ConnectionStringHelper.LookupConnectionString(_connectionString, ConnectionStringKey);
                _client = CloudStorageAccount.Parse(connectionString).CreateCloudBlobClient();
                InternalLogger.Trace("AzureBlobStorageTarget - Initialized");
            }
            catch (Exception ex)
            {
                InternalLogger.Error(ex, "AzureBlobStorageTarget(Name={0}): Failed to create BlobClient with connectionString={1}.", Name, connectionString);
                throw;
            }
        }

        /// <summary>
        /// Writes logging event to the log target.
        /// classes.
        /// </summary>
        /// <param name="logEvent">Logging event to be written out.</param>
        protected override void Write(LogEventInfo logEvent)
        {
            if (string.IsNullOrEmpty(logEvent.Message))
                return;

            var containerName = RenderLogEvent(Container, logEvent);
            var blobName = RenderLogEvent(BlobName, logEvent);

            try
            {
                containerName = CheckAndRepairContainerName(containerName);
                blobName = CheckAndRepairBlobNamingRules(blobName);

                var layoutMessage = RenderLogEvent(Layout, logEvent) ?? string.Empty;
                var logMessageBytes = GenerateLogMessageBytes(layoutMessage, Environment.NewLine);
                
                InitializeContainer(containerName);
                AppendBlobFromByteArray(blobName, logMessageBytes);
            }
            catch (StorageException ex)
            {
                InternalLogger.Error(ex, "AzureBlobStorageTarget: failed writing to blob: {0} in container: {1}", blobName, containerName);
                throw;
            }
        }

        /// <summary>
        /// Writes an array of logging events to the log target. By default it iterates on all
        /// events and passes them to "Write" method. Inheriting classes can use this method to
        /// optimize batch writes.
        /// </summary>
        /// <param name="logEvents">Logging events to be written out.</param>
        protected override void Write(IList<AsyncLogEventInfo> logEvents)
        {
            if (logEvents.Count <= 1)
            {
                base.Write(logEvents);
                return;
            }

            //must sort into containers and then into the blobs for the container
            if (_getContainerBlobNameDelegate == null)
                _getContainerBlobNameDelegate = c => new ContainerBlobKey(RenderLogEvent(Container, c.LogEvent), RenderLogEvent(BlobName, c.LogEvent));

            var blobBuckets = SortHelpers.BucketSort(logEvents, _getContainerBlobNameDelegate);

            //Iterate over all the containers being written to
            foreach (var blobBucket in blobBuckets)
            {
                var containerName = blobBucket.Key.ContainerName;
                var blobName = blobBucket.Key.BlobName;

                _reusableEncodingBuilder.Length = 0;

                try
                {
                    containerName = CheckAndRepairContainerName(containerName);

                    InitializeContainer(containerName);

                    //add each message for the destination append blob
                    foreach (var asyncLogEventInfo in blobBucket.Value)
                    {
                        var layoutMessage = RenderLogEvent(Layout, asyncLogEventInfo.LogEvent);
                        _reusableEncodingBuilder.AppendLine(layoutMessage);
                    }

                    blobName = CheckAndRepairBlobNamingRules(blobBucket.Key.BlobName);
                    var logMessageBytes = GenerateLogMessageBytes(_reusableEncodingBuilder);
                    AppendBlobFromByteArray(blobName, logMessageBytes);

                    foreach (var asyncLogEventInfo in blobBucket.Value)
                        asyncLogEventInfo.Continuation(null);
                }
                catch (StorageException ex)
                {
                    InternalLogger.Error(ex, "AzureBlobStorageTarget: failed writing batch to blob: {0} in container: {1}", blobName, containerName);
                    throw;
                }
                finally
                {
                    const int MaxSize = 512 * 1024;
                    if (_reusableEncodingBuilder.Length > MaxSize)
                    {
                        _reusableEncodingBuilder.Remove(MaxSize, _reusableEncodingBuilder.Length - MaxSize);   // Releases all buffers
                    }
                }
            }
        }

        /// <summary>
        /// Initializes the BLOB.
        /// </summary>
        /// <param name="blobName">Name of the BLOB.</param>
        private void InitializeBlob(string blobName)
        {
            if (_appendBlob == null || _appendBlob.Name != blobName)
            {
                _appendBlob = _container.GetAppendBlobReference(blobName);

#if NETSTANDARD
                var blobExists = _appendBlob.ExistsAsync().GetAwaiter().GetResult();
#else
                var blobExists = _appendBlob.Exists();
#endif
                if (!blobExists)
                {
                    _appendBlob.Properties.ContentType = ContentType;

#if NETSTANDARD
                    _appendBlob.CreateOrReplaceAsync().GetAwaiter().GetResult();
#else
                    _appendBlob.CreateOrReplace(AccessCondition.GenerateIfNotExistsCondition());
#endif
                }
            }
        }

        /// <summary>
        /// Initializes the Azure storage container and creates it if it doesn't exist.
        /// </summary>
        /// <param name="containerName">Name of the container.</param>
        private void InitializeContainer(string containerName)
        {
            if (_container == null || _container.Name != containerName)
            {
                try
                {
                    _container = _client.GetContainerReference(containerName);
#if NETSTANDARD
                    if (!_container.ExistsAsync().GetAwaiter().GetResult())
                    {
                        _container.CreateIfNotExistsAsync().GetAwaiter().GetResult();
                    }
#else
                    if (!_container.Exists())
                    {
                        _container.CreateIfNotExists();
                    }
#endif
                }
                catch (StorageException storageException)
                {
                    InternalLogger.Error(storageException, "AzureBlobStorageTarget(Name={0}): Failed to create reference to container: {1}.", Name, containerName);
                    throw;
                }

                _appendBlob = null;
            }
        }

        private void AppendBlobFromByteArray(string blobName, byte[] buffer)
        {
            InitializeBlob(blobName);

#if NETSTANDARD
            _appendBlob.AppendFromByteArrayAsync(buffer, 0, buffer.Length).GetAwaiter().GetResult();
#else
            _appendBlob.AppendFromByteArray(buffer, 0, buffer.Length);
#endif
        }

        /// <summary>
        /// Skips string.ToCharArray allocation (if possible)
        /// </summary>
        private byte[] GenerateLogMessageBytes(string layoutMessage, string newLine)
        {
            newLine = newLine ?? string.Empty;
            var totalLength = layoutMessage.Length + newLine.Length;
            if (totalLength < _reusableEncodingBuffer.Length)
            {
                layoutMessage.CopyTo(0, _reusableEncodingBuffer, 0, layoutMessage.Length);
                newLine.CopyTo(0, _reusableEncodingBuffer, layoutMessage.Length, newLine.Length);
                return Encoding.UTF8.GetBytes(_reusableEncodingBuffer, 0, totalLength);
            }
            else
            {
                return Encoding.UTF8.GetBytes(string.Concat(layoutMessage, Environment.NewLine));
            }
        }

        /// <summary>
        /// Skips ToString-allocation and string.ToCharArray allocation (if possible)
        /// </summary>
        private byte[] GenerateLogMessageBytes(StringBuilder layoutMessage)
        {
            var totalLength = layoutMessage.Length;
            if (totalLength < _reusableEncodingBuffer.Length)
            {
                layoutMessage.CopyTo(0, _reusableEncodingBuffer, 0, layoutMessage.Length);
                return Encoding.UTF8.GetBytes(_reusableEncodingBuffer, 0, totalLength);
            }
            else
            {
                return Encoding.UTF8.GetBytes(layoutMessage.ToString());
            }
        }

        private string CheckAndRepairContainerName(string containerName)
        {
            return _containerNameCache.LookupStorageName(containerName, _checkAndRepairContainerNameDelegate);
        }

        private string CheckAndRepairContainerNamingRules(string containerName)
        {
            InternalLogger.Trace("AzureBlobStorageTarget(Name={0}): Requested Container Name: {1}", Name, containerName);
            var validContainerName = AzureStorageNameCache.CheckAndRepairContainerNamingRules(containerName);
            if (validContainerName == containerName.ToLowerInvariant())
            {
                InternalLogger.Trace("AzureBlobStorageTarget(Name={0}): Using Container Name: {0}", Name, validContainerName);
            }
            else
            {
                InternalLogger.Trace("AzureBlobStorageTarget(Name={0}): Using Cleaned Container name: {0}", Name, validContainerName);
            }
            return validContainerName;
        }

        /// <summary>
        /// Checks the and repairs BLOB name acording to the Azure naming rules.
        /// </summary>
        /// <param name="blobName">Name of the BLOB.</param>
        /// <returns></returns>
        private static string CheckAndRepairBlobNamingRules(string blobName)
        {
            /*  https://docs.microsoft.com/en-us/rest/api/storageservices/fileservices/naming-and-referencing-containers--blobs--and-metadata
                Blob Names

                A blob name must conforming to the following naming rules:
                A blob name can contain any combination of characters.
                A blob name must be at least one character long and cannot be more than 1,024 characters long.
                Blob names are case-sensitive.
                Reserved URL characters must be properly escaped.

                The number of path segments comprising the blob name cannot exceed 254.
                A path segment is the string between consecutive delimiter characters (e.g., the forward slash '/') that corresponds to the name of a virtual directory.
            */
            if (string.IsNullOrWhiteSpace(blobName) || blobName.Length > 1024)
            {
                var blobDefault = string.Concat("Log-", DateTime.UtcNow.ToString("yy-MM-dd"), ".log");
                InternalLogger.Error("AzureBlobStorageTarget: Invalid Blob Name provided: {0} | Using default: {1}", blobName, blobDefault);
                return blobDefault;
            }
            InternalLogger.Trace("AzureBlobStorageTarget: Using provided blob name: {0}", blobName);
            return blobName;
        }
    }
}
