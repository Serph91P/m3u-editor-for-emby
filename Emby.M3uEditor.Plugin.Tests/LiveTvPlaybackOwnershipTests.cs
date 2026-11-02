using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Emby.M3uEditor.Plugin.Service;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Session;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class LiveTvPlaybackOwnershipTests
    {
        [Fact]
        public void RenegotiatedStop_ClaimsOnlyOriginalRegistration_ExactlyOnce()
        {
            using (var stream = Stream())
            using (var session = Session())
            {
                stream.AddConsumer("original");
                stream.AddConsumer("probe");
                stream.BindPlaybackSession(session, "original", new[] { session });
                Register(session, "replacement", stream.MediaSource.Id);
                var stopped = session.GetPlaySessionInfo("replacement");
                session.RemovePlaySession("replacement");
                var claims = new string[100];
                Parallel.For(0, claims.Length, i => claims[i] = stream.TakeRenegotiatedConsumer(session, "replacement"));
                Assert.Single(claims.Where(id => id == "original"));
                Assert.Equal(2, stream.ConsumerCount); // The registry still owns RemoveConsumer/Close.
                stream.RemoveConsumer("original");
                Assert.Equal(1, stream.ConsumerCount);
                stream.BindPlaybackSession(session, "replacement", new[] { session }, stopped);
                Assert.Null(stream.TakeRenegotiatedConsumer(session, "replacement"));
                stream.RemoveConsumer("probe");
                Assert.Equal(0, stream.ConsumerCount);
            }
        }

        [Theory]
        [InlineData("foreign-session")]
        [InlineData("original-active")]
        [InlineData("replacement-active")]
        [InlineData("normal-stop")]
        [InlineData("empty-stop")]
        [InlineData("already-released")]
        [InlineData("registered-stop")]
        public void StopWithoutRenegotiatedOwnership_CannotClaimConsumer(string scenario)
        {
            using (var stream = Stream())
            using (var session = Session())
            using (var foreign = Session())
            {
                stream.AddConsumer("original");
                stream.BindPlaybackSession(session, "original", new[] { session });
                var stoppedId = "replacement";
                if (scenario == "original-active") Register(session, "original", stream.MediaSource.Id);
                if (scenario == "replacement-active") Register(session, "replacement", stream.MediaSource.Id);
                if (scenario == "normal-stop") stoppedId = "original";
                if (scenario == "empty-stop") stoppedId = null;
                if (scenario == "already-released") stream.RemoveConsumer("original");
                if (scenario == "registered-stop") stream.AddConsumer(stoppedId);
                var count = stream.ConsumerCount;
                Assert.Null(stream.TakeRenegotiatedConsumer(scenario == "foreign-session" ? foreign : session, stoppedId));
                Assert.Equal(count, stream.ConsumerCount);
            }
        }

        [Theory]
        [InlineData("active-owner")]
        [InlineData("replacement-owner")]
        [InlineData("probe")]
        [InlineData("duplicate")]
        [InlineData("legacy-count")]
        [InlineData("unregistered-start")]
        public void PreStartNegotiation_DoesNotInferAmbiguousOwnership(string scenario)
        {
            using (var stream = Stream())
            using (var session = Session())
            using (var other = Session())
            {
                stream.AddConsumer(scenario == "probe" ? null : "original");
                if (scenario == "duplicate") stream.AddConsumer("original");
                if (scenario == "legacy-count") stream.ConsumerCount = 2;
                if (scenario == "active-owner") Register(other, "original", stream.MediaSource.Id);
                if (scenario == "replacement-owner") Register(other, "other-replacement", stream.MediaSource.Id);
                if (scenario != "unregistered-start") Register(session, "replacement", stream.MediaSource.Id);
                stream.BindPlaybackSession(session, "replacement", new[] { session, other });
                session.RemovePlaySession("replacement");
                Assert.Null(stream.TakeRenegotiatedConsumer(session, "replacement"));
            }
        }

        [Fact]
        public void PreStartNegotiation_BindsSoleAbandonedOpenToSelectedNativeSession()
        {
            using (var stream = Stream())
            using (var session = Session())
            {
                stream.AddConsumer("original");
                Register(session, "replacement", stream.MediaSource.Id);
                stream.BindPlaybackSession(session, "replacement", new[] { session });
                session.RemovePlaySession("replacement");
                Assert.Equal("original", stream.TakeRenegotiatedConsumer(session, "replacement"));
            }
        }

        [Fact]
        public void AdditionalStop_PreservesStartedReplacementUntilItsOwnFinalStop()
        {
            using (var stream = Stream())
            using (var session = Session())
            {
                stream.AddConsumer("original");
                stream.BindPlaybackSession(session, "original", new[] { session });
                Register(session, "replacement", stream.MediaSource.Id);
                stream.BindPlaybackSession(session, "replacement", new[] { session });
                var replacement = session.GetPlaySessionInfo("replacement");
                Register(session, "additional", stream.MediaSource.Id);
                session.RemovePlaySession("additional");
                Assert.Null(session.PlayState.MediaSourceId); // Only the last-session projection is empty.
                Assert.Equal(stream.MediaSource.Id, replacement.PlayState.MediaSourceId);
                Assert.Null(stream.TakeRenegotiatedConsumer(session, "additional"));
                Assert.Equal(1, stream.ConsumerCount);
                session.RemovePlaySession("replacement");
                Assert.Equal("original", stream.TakeRenegotiatedConsumer(session, "replacement"));
            }
        }

        private static M3uEditorLiveStream Stream() => new M3uEditorLiveStream(new MediaSourceInfo { Id = "synthetic-channel" }, "synthetic-tuner", new HttpClient());
        private static SessionInfo Session() => new SessionInfo(null, DispatchProxy.Create<ILogger, OwnershipLogger>());
        private static void Register(SessionInfo session, string id, string sourceId) => session.RegisterPlaySession(new PlaybackStartInfo { PlaySessionId = id, MediaSourceId = sourceId });
    }

    public class OwnershipLogger : DispatchProxy
    {
        protected override object Invoke(MethodInfo method, object[] args) => method.ReturnType == typeof(void) ? null : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
