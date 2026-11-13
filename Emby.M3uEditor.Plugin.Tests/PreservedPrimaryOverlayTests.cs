using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Emby.M3uEditor.Plugin.Client;
using Emby.M3uEditor.Plugin.Client.Models;
using Emby.M3uEditor.Plugin.Service;
using Xunit;

namespace Emby.M3uEditor.Plugin.Tests
{
    public class PreservedPrimaryOverlayTests
    {
        internal static EpgProgram Program(bool xml = true) => new EpgProgram {
            Title = xml ? "Synthetic" : Convert.ToBase64String(Encoding.UTF8.GetBytes("Synthetic")), IsPlainText = xml,
            StartTimestamp=100, StopTimestamp=200, SubTitle= xml ? "Episode" : null,
            SourcePrimaryIconUrl=xml ? "https://example.invalid/primary.jpg" : null,
            SourcePrimaryIconWidth=1920, SourcePrimaryIconHeight=1080,
            PosterUrl="https://example.invalid/poster.jpg", PosterWidth=500, PosterHeight=750,
            ThumbImageUrl="https://example.invalid/primary.jpg", ThumbWidth=1920, ThumbHeight=1080,
            BackdropUrl="https://example.invalid/backdrop.jpg", ChannelId="raw", Description="Description", Id="id", EpgId="epg",
            ContentId="film", SeriesId="series", Language="en", HasArchive=true, IsLive=true, IsNew=true, IsPremiere=true,
            IsPreviouslyShown=true, Categories=new List<string>{"drama"}, Start="start", End="end", LogoImageUrl="https://example.invalid/logo.jpg"
        };
        internal static PreservedPrimaryEntry Entry(bool xml = true) => new PreservedPrimaryEntry {
            StreamId=1, StartTimestamp=100, StopTimestamp=200, Title="Synthetic", Subtitle=xml ? "Episode" : null,
            Guide=new PreservedPrimaryGuide { PrimaryIconUrl=xml ? "https://example.invalid/primary.jpg" : null,
                PosterUrl="https://example.invalid/poster.jpg", PosterWidth=500, PosterHeight=750,
                StillUrl="https://example.invalid/primary.jpg", StillWidth=1920, StillHeight=1080, BackdropUrl="https://example.invalid/backdrop.jpg" },
            Primary=new PreservedPrimaryImage { Url="https://example.invalid/primary.jpg", Width=1920, Height=1080 }
        };

        [Theory][InlineData(true)][InlineData(false)]
        public void ExactTupleCopiesOnlyProtectedProgrammeAndKeepsMetadata(bool xml)
        {
            var original=Program(xml); var before=JsonSerializer.Serialize(original);
            var copy=Assert.Single(PreservedPrimaryOverlay.Apply(new[]{original},1,new[]{Entry(xml)},xml ? "xmltv" : "json"));
            Assert.NotSame(original,copy); Assert.Null(original.PreservedPrimaryUrl);
            Assert.Equal(before,JsonSerializer.Serialize(original));
            foreach(var property in typeof(EpgProgram).GetProperties().Where(p=>!p.Name.StartsWith("Preserved") && !p.Name.StartsWith("Projected")))
                Assert.Equal(property.GetValue(original),property.GetValue(copy));
            var info=M3uEditorTunerHost.BuildProgramInfo(copy,1,"one","Synthetic","Description");
            Assert.Equal("https://example.invalid/primary.jpg",info.ImageUrl);
            Assert.Equal(1920,info.ImageWidth); Assert.Equal(1080,info.ImageHeight);
            Assert.Equal(original.BackdropUrl,info.BackdropImageUrl); Assert.Equal(original.ThumbImageUrl,info.ThumbImageUrl);
            var other=Assert.Single(PreservedPrimaryOverlay.Apply(new[]{original},2,new[]{Entry(xml)},xml ? "xmltv" : "json"));
            Assert.Same(original,other);
        }

        [Theory]
        [InlineData("stream")][InlineData("start")][InlineData("stop")][InlineData("title")][InlineData("subtitle")]
        [InlineData("poster")][InlineData("poster-width")][InlineData("still")][InlineData("still-width")][InlineData("backdrop")]
        [InlineData("icon")][InlineData("invalid")][InlineData("url-case")][InlineData("conflicting")][InlineData("legacy")]
        public void MismatchedOrConflictingEvidenceDoesNotProtect(string change)
        {
            var p=Program(); var e=Entry(); var entries=new List<PreservedPrimaryEntry>{e};
            switch(change) {
                case "stream": e.StreamId=2; break; case "start": e.StartTimestamp++; break; case "stop": e.StopTimestamp++; break;
                case "title": e.Title="synthetic"; break; case "subtitle": e.Subtitle="other"; break;
                case "poster": e.Guide.PosterUrl+="?changed"; break; case "poster-width": e.Guide.PosterWidth++; break;
                case "still": e.Guide.StillUrl+="?changed"; break; case "still-width": e.Guide.StillWidth++; break;
                case "backdrop": e.Guide.BackdropUrl+="?changed"; break; case "icon": e.Guide.PrimaryIconUrl+="?changed"; break;
                case "invalid": e.Primary.Url="file:///primary"; break;
                case "url-case": e.Guide.PrimaryIconUrl="https://EXAMPLE.invalid/primary.jpg"; break;
                case "conflicting": var rival=Entry(); rival.Primary.Width=100; entries.Add(rival); break;
                case "legacy": p.PreservedPrimaryUrl="https://example.invalid/legacy.jpg"; break;
            }
            var conflicts=0;
            Assert.Same(p,Assert.Single(PreservedPrimaryOverlay.Apply(new[]{p},1,entries,"xmltv",()=>conflicts++)));
            Assert.Equal(change=="legacy" ? 1 : 0,conflicts);
        }

        [Theory][InlineData(false)][InlineData(true)]
        public void UnknownOrContradictoryPrimaryGeometryNeverBorrowsPoster(bool conflict)
        {
            var p=Program(false); var e=Entry(false);
            if(conflict) { e.Primary.Width=1280; e.Primary.Height=720; }
            else { e.Primary.Url="https://example.invalid/unclassified.jpg"; e.Primary.Width=0; e.Primary.Height=0; }
            var copy=Assert.Single(PreservedPrimaryOverlay.Apply(new[]{p},1,new[]{e},"json"));
            var info=M3uEditorTunerHost.BuildProgramInfo(copy,1,"one","Synthetic","");
            Assert.Equal(e.Primary.Url,info.ImageUrl); Assert.Equal(0,info.ImageWidth); Assert.Equal(0,info.ImageHeight);
        }

        [Fact]
        public void ConflictingProgrammeOccurrencesAndJsonInventedFieldsAbstain()
        {
            var p=Program(false); var rival=Program(false); rival.ThumbImageUrl="https://example.invalid/other.jpg";
            Assert.All(PreservedPrimaryOverlay.Apply(new[]{p,rival},1,new[]{Entry(false)},"json"),v=>Assert.Null(v.PreservedPrimaryUrl));
            var e=Entry(false); e.Guide.PrimaryIconUrl=e.Primary.Url;
            Assert.Same(p,Assert.Single(PreservedPrimaryOverlay.Apply(new[]{p},1,new[]{e},"json")));
            e.Guide.PrimaryIconUrl=null; e.Subtitle="invented";
            Assert.Same(p,Assert.Single(PreservedPrimaryOverlay.Apply(new[]{p},1,new[]{e},"json")));
        }
    }
}
