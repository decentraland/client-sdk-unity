using System.Collections.Generic;
using LiveKit.Internal.FFIClients;
using LiveKit.Internal.FFIClients.Pools;
using LiveKit.Internal.FFIClients.Requests;
using LiveKit.Proto;
using NUnit.Framework;

namespace LiveKit.Tests
{
    public class LoopbackIceTransportTest
    {
        private bool previousFlag;
        private RecordingFfiClient client = null!;
        private FFIBridge bridge = null!;

        [SetUp]
        public void SetUp()
        {
            previousFlag = FFIBridgeExtensions.UseTransportAllForLoopbackUrls;
            client = new RecordingFfiClient();
            // A single pool is shared across connects, the same way FFIBridge.Instance shares one in production.
            bridge = new FFIBridge(client, new ThreadSafeMultiPool());
        }

        [TearDown]
        public void TearDown()
        {
            FFIBridgeExtensions.UseTransportAllForLoopbackUrls = previousFlag;
        }

        [Test]
        [TestCase("ws://localhost:7880")]
        [TestCase("wss://LOCALHOST/rtc")]
        [TestCase("ws://127.0.0.1:7880")]
        [TestCase("ws://[::1]:7880")]
        public void LoopbackUrlUsesTransportAllWhenEnabled(string url)
        {
            FFIBridgeExtensions.UseTransportAllForLoopbackUrls = true;

            Connect(url);

            ConnectRequest sent = client.Sent[0];
            Assert.IsNotNull(sent.Options.RtcConfig);
            Assert.AreEqual(IceTransportType.TransportAll, sent.Options.RtcConfig.IceTransportType);
        }

        [Test]
        [TestCase("wss://livekit.decentraland.org")]
        [TestCase("wss://localhost.example.com")]
        [TestCase("wss://127.0.0.2")]
        [TestCase("https://localhost")]
        [TestCase("http://127.0.0.1:7880")]
        [TestCase("not a url")]
        [TestCase("")]
        public void NonLoopbackUrlDoesNotSetRtcConfig(string url)
        {
            FFIBridgeExtensions.UseTransportAllForLoopbackUrls = true;

            Connect(url);

            Assert.IsNull(client.Sent[0].Options.RtcConfig);
        }

        [Test]
        public void LoopbackUrlDoesNotSetRtcConfigWhenDisabled()
        {
            FFIBridgeExtensions.UseTransportAllForLoopbackUrls = false;

            Connect("ws://localhost:7880");

            Assert.IsNull(client.Sent[0].Options.RtcConfig);
        }

        [Test]
        public void TransportAllDoesNotLeakIntoLaterRemoteConnect()
        {
            FFIBridgeExtensions.UseTransportAllForLoopbackUrls = true;

            Connect("ws://localhost:7880");
            Connect("wss://livekit.decentraland.org");

            Assert.AreEqual(2, client.Sent.Count);
            Assert.AreEqual(IceTransportType.TransportAll, client.Sent[0].Options.RtcConfig.IceTransportType);
            Assert.IsNull(client.Sent[1].Options.RtcConfig, "Remote connect must not inherit the pooled loopback RtcConfig");
        }

        private void Connect(string url)
        {
            using FfiResponseWrap response = bridge.SendConnectRequest(url, "token", autoSubscribe: true);
        }

        private class RecordingFfiClient : IFFIClient
        {
            public readonly List<ConnectRequest> Sent = new();

            public void Initialize() { }

            public bool Initialized() => true;

            public FfiResponse SendRequest(FfiRequest request)
            {
                // Snapshot at send time: the pooled request objects are reused after this call returns.
                Sent.Add(request.Connect.Clone());
                return new FfiResponse();
            }

            public void Release(FfiResponse response) { }

            public void Dispose() { }
        }
    }
}
