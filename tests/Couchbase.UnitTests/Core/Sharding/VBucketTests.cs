using System.Collections.Generic;
using System.Linq;
using System.Net;
using Couchbase.Core.Configuration.Server;
using Couchbase.Core.DI;
using Couchbase.Core.Sharding;
using Couchbase.UnitTests.Utils;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Couchbase.UnitTests.Core.Sharding
{
    public class VBucketTests
    {
        private readonly IVBucket _vBucket;
        private readonly ICollection<IPEndPoint> _servers;
        private readonly VBucketServerMap _vBucketServerMap;

        public  VBucketTests()
        {
            var serverConfigJson = ResourceHelper.ReadResource("bootstrap-config.json");
            var bucketConfig = JsonConvert.DeserializeObject<BucketConfig>(serverConfigJson);

            var (vBucketServerMap, _) = GetServerMapAndIpEndPoints(bucketConfig.VBucketServerMap);
            _vBucketServerMap = vBucketServerMap;

            _servers = new List<IPEndPoint>();
            foreach (var node in bucketConfig.GetNodes())
            {
                _servers.Add(new IPEndPoint(IPAddress.Parse(node.Hostname), node.KeyValue));
            }

            var vBucketMap = _vBucketServerMap.VBucketMap.First();
            var primary = vBucketMap[0];
            var replicas = new []{vBucketMap[1]};
            _vBucket = new VBucket(_servers, 0, primary, replicas, bucketConfig.Rev, _vBucketServerMap, "default",
                new Mock<ILogger<VBucket>>().Object);
        }

        [Fact]
        public void TestLocatePrimary()
        {
            var primary = _vBucket.LocatePrimary();
            Assert.NotNull(primary);

            var expected = _servers.First();
            Assert.Equal(expected, primary);
        }

        [Fact]
        public void TestLocateReplica()
        {
            const int replicaIndex = 0;
            var replica = _vBucket.LocateReplica(replicaIndex);
            Assert.NotNull(replica);

            var expected = _vBucketServerMap.IPEndPoints[replicaIndex];
            Assert.Same(expected, replica);
        }

        [Fact]
        public void When_BucketConfig_Has_Replicas_VBucketKeyMapper_Replica_Count_Is_Equal()
        {
            var json = ResourceHelper.ReadResource(@"Data\Configuration\config-with-replicas-complete.json");
            var bucketConfig = JsonConvert.DeserializeObject<BucketConfig>(json);

            var (vBucketServerMap, _) = GetServerMapAndIpEndPoints(bucketConfig.VBucketServerMap);

            var mapper = new VBucketKeyMapper(bucketConfig, vBucketServerMap,
                new VBucketFactory(new Mock<ILogger<VBucket>>().Object));
            var vBucket = (IVBucket)mapper.MapKey("somekey");

            const int expected = 3;
            Assert.Equal(expected, vBucket.Replicas.Count());
        }

        [Fact]
        public void When_BucketConfig_Has_Replicas_VBucketKeyMapper_Replicas_Are_Equal()
        {
            var json = ResourceHelper.ReadResource(@"Data\Configuration\config-with-replicas-complete.json");
            var bucketConfig = JsonConvert.DeserializeObject<BucketConfig>(json);

            var (vBucketServerMap, _) = GetServerMapAndIpEndPoints(bucketConfig.VBucketServerMap);

            var mapper = new VBucketKeyMapper(bucketConfig, vBucketServerMap,
                new VBucketFactory(new Mock<ILogger<VBucket>>().Object));
            var vBucket = (IVBucket)mapper.MapKey("somekey");

            var index = mapper.GetIndex("somekey");
            var expected = bucketConfig.VBucketServerMap.VBucketMap[index];
            for (var i = 0; i < vBucket.Replicas.Length; i++)
            {
                Assert.Equal(vBucket.Replicas[i], expected[i+1]);
            }
        }

        [Fact]
        public void When_BucketConfig_Has_Replicas_VBucketKeyMapper_LocateReplica_Returns_Correct_Server()
        {
            var json = ResourceHelper.ReadResource(@"Data\Configuration\config-with-replicas-complete.json");
            var bucketConfig = JsonConvert.DeserializeObject<BucketConfig>(json);

            var (vBucketServerMap, _) = GetServerMapAndIpEndPoints(bucketConfig.VBucketServerMap);

            var mapper = new VBucketKeyMapper(bucketConfig, vBucketServerMap,
                new VBucketFactory(new Mock<ILogger<VBucket>>().Object));
            var vBucket = (IVBucket)mapper.MapKey("somekey");

            foreach (var index in vBucket.Replicas)
            {
                var server = vBucket.LocateReplica(index);
                Assert.NotNull(server);

                var expected = bucketConfig.VBucketServerMap.ServerList[index];
                Assert.Equal(server.Address.ToString(), expected.Split(':').First());
            }
        }

        [Fact]
        public void When_Primary_Is_Negative_Random_Server_Returned()
        {
            var json = ResourceHelper.ReadResource(@"Data\Configuration\config-with-negative-one-primary.json");
            var bucketConfig = JsonConvert.DeserializeObject<BucketConfig>(json);

            var (vBucketServerMap, _) = GetServerMapAndIpEndPoints(bucketConfig.VBucketServerMap);

            var mapper = new VBucketKeyMapper(bucketConfig, vBucketServerMap,
                new VBucketFactory(new Mock<ILogger<VBucket>>().Object));

            //maps to -1 primary
            const string key = "somekey0";
            var vBucket = (IVBucket)mapper.MapKey(key);
            Assert.Equal(-1, vBucket.Primary);

            var primary = vBucket.LocatePrimary();
            Assert.NotNull(primary);
        }

        [Fact]
        public void When_Primary_Index_Is_Greater_Than_Cluster_Count_Random_Server_Returned()
        {
            var json = ResourceHelper.ReadResource(@"Data\Configuration\config-with-negative-one-primary.json");
            var bucketConfig = JsonConvert.DeserializeObject<BucketConfig>(json);

            //remove one server
            bucketConfig.NodesExt.Remove(bucketConfig.NodesExt.First());
            bucketConfig.Nodes.Remove(bucketConfig.Nodes.First());

            var (vBucketServerMap, _) = GetServerMapAndIpEndPoints(bucketConfig.VBucketServerMap);

            var mapper = new VBucketKeyMapper(bucketConfig, vBucketServerMap,
                new VBucketFactory(new Mock<ILogger<VBucket>>().Object));

            //maps to -1 primary
            const string key = "somekey23";
            var vBucket = (IVBucket)mapper.MapKey(key);

            var primary = vBucket.LocatePrimary();
            Assert.NotNull(primary);
        }

        [Fact]
        public void When_Replica_Index_OOR_LocatePrimary_Returns_Random_Server()
        {
            var (vBucketServerMap, ipEndPoints) = GetServerMapAndIpEndPoints("127.0.0.1:10210");

            var vBucket = new VBucket(ipEndPoints, 100, -1, new short[] {2}, 0,
                vBucketServerMap, "default", new Mock<ILogger<VBucket>>().Object);
            var found = vBucket.LocatePrimary();
            Assert.NotNull(found);
        }

        [Fact]
        public void When_Replica_Index_1_LocatePrimary_Returns_Random_Server()
        {
            var (vBucketServerMap, ipEndPoints) = GetServerMapAndIpEndPoints();

            var vBucket = new VBucket(ipEndPoints, 100, -1, new short[] { 0 }, 0,
                vBucketServerMap, "default", new Mock<ILogger<VBucket>>().Object);
            var found = vBucket.LocatePrimary();
            Assert.Null(found);//should be null
        }

        [Fact]
        public void When_Replica_Index_Negative_LocatePrimary_Returns_Random_Server()
        {
            var (vBucketServerMap, ipEndPoints) = GetServerMapAndIpEndPoints("127.0.0.1:10210");

            var vBucket = new VBucket(ipEndPoints, 100, -1, new short[] { -1 }, 0,
                vBucketServerMap, "default", new Mock<ILogger<VBucket>>().Object);
            var found = vBucket.LocatePrimary();
            Assert.NotNull(found);
        }

        [Fact]
        public void When_Replica_Index_Positive_LocatePrimary_Returns_It()
        {
            var (vBucketServerMap, ipEndPoints) = GetServerMapAndIpEndPoints("127.0.0.1:10210", "127.0.0.2:10210");

            var vBucket = new VBucket(ipEndPoints, 100, -1, new short[] { 0 }, 0,
                vBucketServerMap, "default", new Mock<ILogger<VBucket>>().Object);
            var found = vBucket.LocatePrimary();
            Assert.NotNull(found);
        }

        #region Helpers

        private static (VBucketServerMap serverMap, List<IPEndPoint> ipEndPoints) GetServerMapAndIpEndPoints(
            params string[] servers)
        {
            var vBucketServerMapDto = new VBucketServerMapDto
            {
                ServerList = servers.ToArray()
            };

            var ipEndPoints = vBucketServerMapDto.ServerList
                .Select(p =>
                {
                    var split = p.Split(':');
                    return new IPEndPoint(IPAddress.Parse(split[0]), int.Parse(split[1]));
                })
                .ToList();

            return (new VBucketServerMap(vBucketServerMapDto, ipEndPoints), ipEndPoints);
        }

        private static (VBucketServerMap serverMap, List<IPEndPoint> ipEndPoints) GetServerMapAndIpEndPoints(
            VBucketServerMapDto vBucketServerMapDto)
        {
            var ipEndPoints = vBucketServerMapDto.ServerList
                .Select(p =>
                {
                    var split = p.Split(':');
                    return new IPEndPoint(IPAddress.Parse(split[0]), int.Parse(split[1]));
                })
                .ToList();

            return (new VBucketServerMap(vBucketServerMapDto, ipEndPoints), ipEndPoints);
        }

        #endregion
    }
}
