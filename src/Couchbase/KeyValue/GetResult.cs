using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using Couchbase.Core.IO.Converters;
using Couchbase.Core.IO.Operations;
using Couchbase.Core.IO.Serializers;
using Couchbase.Core.IO.Transcoders;
using Couchbase.Utils;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

#nullable enable

namespace Couchbase.KeyValue
{
    internal class GetResult : IGetResult
    {
        private readonly IMemoryOwner<byte> _contentBytes;
        private readonly IList<LookupInSpec> _specs;
        private readonly List<string>? _projectList;
        private readonly ITypeTranscoder _transcoder;
        private readonly ITypeSerializer _serializer;
        private readonly ILogger<GetResult> _logger;
        private bool _isParsed;
        private TimeSpan? _expiry;

        internal GetResult(IMemoryOwner<byte> contentBytes, ITypeTranscoder transcoder, ILogger<GetResult> logger,
            List<LookupInSpec>? specs = null, List<string>? projectList = null)
        {
            _contentBytes = contentBytes;
            _transcoder = transcoder ?? throw new ArgumentNullException(nameof(transcoder));
            _serializer = transcoder.Serializer;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _specs = specs ?? (IList<LookupInSpec>) Array.Empty<LookupInSpec>();
            _projectList = projectList;
        }

        internal OperationHeader Header { get; set; }

        internal OpCode OpCode { get; set; }

        internal Flags Flags { get; set; }

        public string? Id { get; internal set; }

        public ulong Cas { get; internal set; }

        public TimeSpan? Expiry
        {
            get
            {
                ParseSpecs();
                if (_expiry.HasValue)
                {
                    return _expiry;
                }

                var spec = _specs.FirstOrDefault(x => x.Path == VirtualXttrs.DocExpiryTime);
                if (spec != null)
                {
                    var ms = _serializer.Deserialize<long>(spec.Bytes);
                    _expiry = TimeSpan.FromMilliseconds(ms);
                }

                return _expiry;
            }
        }

        internal uint Opaque { get; set; }

        public T ContentAs<T>()
        {
            EnsureNotDisposed();

            //basic GET or other non-projection operation
            if (OpCode == OpCode.Get || OpCode == OpCode.ReplicaRead || OpCode == OpCode.GetL || OpCode == OpCode.GAT)
            {
                return _transcoder.Decode<T>(_contentBytes.Memory, Flags, OpCode);
            }

            //oh mai, its a projection
            ParseSpecs();

            // normal GET
            if (_specs.Count == 1 && _projectList?.Count == 0)
            {
                var spec = _specs[0];
                return _transcoder.Decode<T>(spec.Bytes, Flags, OpCode.Get);
            }

            var root = new JObject();
            foreach (var spec in _specs)
            {
                //skip the expiry if it was included; it must fetched from this.Expiry
                if (spec.Path == VirtualXttrs.DocExpiryTime)
                {
                    continue;
                }
                var content = _serializer.Deserialize<JToken>(spec.Bytes);
                if (spec.OpCode == OpCode.Get)
                {
                    //a full doc is returned if the projection count exceeds the server limit
                    //so we remove any non-requested fields from the content returned.
                    if (_projectList?.Count > 16)
                    {
                        foreach (var child in content.Children())
                        {
                            if (_projectList.Contains(child.Path))
                            {
                                root.Add(child);
                            }
                        }

                        //root projection for empty path
                        return root.ToObject<T>();
                    }
                }
                var projection = CreateProjection(spec.Path, content);

                try
                {
                    root.Add(projection.First);
                }
                catch (Exception e)
                {
                    //these are cases where a root attribute is already mapped
                    //for example "attributes" and "attributes.hair" will cause exceptions
                    _logger.LogInformation(e, "Deserialization failed.");
                }
            }

            if (root.Path == string.Empty && typeof(T).IsPrimitive)
            {
                return root.First.ToObject<T>();
            }
            return root.ToObject<T>();
        }

        private void ParseSpecs()
        {
            //we already parsed the response from the server but not each element
            if(_isParsed) return;

            var response = _contentBytes.Memory;
            var commandIndex = 0;

            for (;;)
            {
                var bodyLength = ByteConverter.ToInt32(response.Span.Slice(2));
                var payLoad = response.Slice(6, bodyLength);

                var command = _specs[commandIndex++];
                command.Status = (ResponseStatus)ByteConverter.ToUInt16(response.Span);
                command.ValueIsJson = payLoad.Span.IsJson();
                command.Bytes = payLoad;

                response = response.Slice(6 + bodyLength);

                if (response.Length <= 0) break;
            }

            _isParsed = true;
        }

        void BuildPath(JToken token, string name, JToken? content = null)
        {
            foreach (var child in token.Children())
            {
                if (child is JValue value)
                {
                    value.Replace(new JObject(new JProperty(name, content)));
                    break;
                }
                BuildPath(child, name, content);
            }
        }

        JObject CreateProjection(string path, JToken content)
        {
            var elements = path.Split('.');
            var projection = new JObject();
            if (elements.Length == 1)
            {
                projection.Add(new JProperty(elements[0], content));
            }
            else
            {
                for (var i = 0; i < elements.Length; i++)
                {
                    if (projection.Last != null)
                    {
                        if (i == elements.Length - 1)
                        {
                            BuildPath(projection, elements[i], content);
                            continue;
                        }

                        BuildPath(projection, elements[i]);
                        continue;
                    }

                    projection.Add(new JProperty(elements[i], null));
                }
            }

            return projection;
        }

        #region Finalization and Dispose

        ~GetResult()
        {
            Dispose(false);
        }

        private bool _disposed;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            _disposed = true;
            _contentBytes?.Dispose();
        }

        protected void EnsureNotDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }
        }

        #endregion
    }
}
