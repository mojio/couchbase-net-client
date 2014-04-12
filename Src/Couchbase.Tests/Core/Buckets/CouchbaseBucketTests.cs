﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using Couchbase.Configuration;
using Couchbase.Configuration.Client;
using Couchbase.Core;
using Couchbase.Core.Buckets;
using Couchbase.Views;
using NUnit.Framework;
using Wintellect;

namespace Couchbase.Tests.Core.Buckets
{
    [TestFixture]
    public class CouchbaseBucketTests
    {
        private ICluster _cluster;
        private IBucket _bucket;

        [TestFixtureSetUp]
        public void TestFixtureSetUp()
        {
            _cluster = new Cluster();
        }

        [Test]
        public void Test_GetBucket()
        {
            var bucket = _cluster.OpenBucket("default");
            Assert.AreEqual("default", bucket.Name);
            _cluster.CloseBucket(bucket);
        }

        [Test]
        [ExpectedException(typeof(ConfigException))]
        public void Test_That_GetBucket_Throws_ConfigException_If_Bucket_Does_Not_Exist()
        {
            var bucket = _cluster.OpenBucket("doesnotexist");
            Assert.AreEqual("doesnotexist", bucket.Name);
            _cluster.CloseBucket(bucket);
        }

        [Test]
        public void Test_View_Query()
        {
            var bucket = (IViewSupportable)_cluster.OpenBucket("beer-sample");
            var query = new ViewQuery(true).
                From("beer-sample", "beers").
                View("by_name");

            var result = bucket.Get<dynamic>(query);
            Assert.Greater(result.TotalRows, 0);
            _cluster.CloseBucket((IBucket)bucket);
        }

        [Test]
        public void Test_View_Query_Lots()
        {
            var bucket = (IViewSupportable)_cluster.OpenBucket("beer-sample");
            var query = new ViewQuery(false).
                From("beer-sample", "beer").
                View("brewery_beers");

            var result = bucket.Get<dynamic>(query);
            for (var i = 0; i < 10; i++)
            {
                using (new OperationTimer())
                {       
                    Assert.Greater(result.TotalRows, 0);
                }
            }
            _cluster.CloseBucket((IBucket)bucket);
        }

        [Test]
        public void Test_N1QL_Query()
        {
            var bucket = (ICouchbaseBucket) _cluster.OpenBucket("default");
            const string query = "SELECT * FROM tutorial WHERE fname = 'Ian'";

            var result = bucket.Query<dynamic>(query);
            foreach (var row in result.Rows)
            {
                Console.WriteLine(row);
            }
            _cluster.CloseBucket(bucket);
        }

        [Test]
        public void When_Valid_Credentials_Provided_Bucket_Created_Succesfully()
        {
            var cluster = new Cluster(new ClientConfiguration
            {
                BucketConfigs = new List<BucketConfiguration>
                {
                    new BucketConfiguration
                    {
                        BucketName = "authenticated"
                    }
                }
            });
            var bucket = cluster.OpenBucket("authenticated", "secret");
            Assert.IsNotNull(bucket);
        }

        [Test]
        [ExpectedException(typeof(AuthenticationException))]
        public void When_InValid_Credentials_Provided_Bucket_Created_UnSuccesfully()
        {
            var cluster = new Cluster(new ClientConfiguration
            {
                BucketConfigs = new List<BucketConfiguration>
                {
                    new BucketConfiguration
                    {
                        BucketName = "authenticated"
                    }
                }
            });
            var bucket = cluster.OpenBucket("authenticated", "secretw");
            Assert.IsNotNull(bucket);
        }

        [TestFixtureTearDown]
        public void TestFixtureTearDown()
        {
            _cluster.Dispose();
        }
    }
}
