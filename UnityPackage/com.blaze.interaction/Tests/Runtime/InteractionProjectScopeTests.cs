using System.IO;
using Blaze.Interaction.Internal;
using NUnit.Framework;

namespace Blaze.Interaction.Tests
{
    public sealed class InteractionProjectScopeTests
    {
        [Test]
        public void EditorScope_UsesProjectLibraryAndStableScopedPipe()
        {
            var scope = InteractionProjectScopeResolver.Resolve(
                true,
                @"E:\ProjectA\Assets",
                @"C:\PlayerData",
                "Blaze.InteractionBridge",
                "");

            Assert.That(scope.DataRoot, Is.EqualTo(Path.GetFullPath(@"E:\ProjectA\Library\BlazeInteraction")));
            Assert.That(scope.ProfilePath, Is.Null);
            Assert.That(scope.PipeName, Does.StartWith("Blaze.InteractionBridge."));
            Assert.That(scope.PipeName, Is.EqualTo(InteractionProjectScopeResolver.Resolve(
                true, @"E:\ProjectA\Assets", @"C:\Other", "Blaze.InteractionBridge", "").PipeName));
        }

        [Test]
        public void DifferentProjects_GetDifferentPipesAndDataRoots()
        {
            var a = ResolveEditor(@"E:\ProjectA\Assets");
            var b = ResolveEditor(@"E:\ProjectB\Assets");

            Assert.That(a.DataRoot, Is.Not.EqualTo(b.DataRoot));
            Assert.That(a.PipeName, Is.Not.EqualTo(b.PipeName));
        }

        [Test]
        public void PlayerScope_UsesPersistentDataPathAndNormalizesCaseInsensitiveIdentity()
        {
            var upper = InteractionProjectScopeResolver.Resolve(
                false,
                @"E:\Unused\Assets",
                @"C:\PlayerData",
                "Blaze.InteractionBridge",
                "");
            var lower = InteractionProjectScopeResolver.Resolve(
                false,
                @"E:\Unused\Assets",
                @"c:\playerdata",
                "Blaze.InteractionBridge",
                "");

            Assert.That(upper.DataRoot, Is.EqualTo(Path.GetFullPath(@"C:\PlayerData\BlazeInteraction")));
            Assert.That(upper.PipeName, Is.EqualTo(lower.PipeName));
            Assert.That(upper.PipeName, Does.Match("^Blaze\\.InteractionBridge\\.[0-9A-F]{16}$"));
        }

        [Test]
        public void ProfileOverride_ResolvesRelativeToEnvironmentAndPreservesAbsolutePath()
        {
            var relativeEditor = InteractionProjectScopeResolver.Resolve(
                true,
                @"E:\ProjectA\Assets",
                @"C:\PlayerData",
                "Blaze.InteractionBridge",
                @"Profiles\radar.json");
            var relativePlayer = InteractionProjectScopeResolver.Resolve(
                false,
                @"E:\ProjectA\Assets",
                @"C:\PlayerData",
                "Blaze.InteractionBridge",
                @"Profiles\radar.json");
            var absolute = InteractionProjectScopeResolver.Resolve(
                true,
                @"E:\ProjectA\Assets",
                @"C:\PlayerData",
                "Blaze.InteractionBridge",
                @"D:\Profiles\radar.json");

            Assert.That(relativeEditor.ProfilePath, Is.EqualTo(Path.GetFullPath(@"E:\ProjectA\Profiles\radar.json")));
            Assert.That(relativePlayer.ProfilePath, Is.EqualTo(Path.GetFullPath(@"C:\PlayerData\Profiles\radar.json")));
            Assert.That(absolute.ProfilePath, Is.EqualTo(Path.GetFullPath(@"D:\Profiles\radar.json")));
        }

        private static InteractionProjectScope ResolveEditor(string assetsPath)
        {
            return InteractionProjectScopeResolver.Resolve(
                true,
                assetsPath,
                @"C:\PlayerData",
                "Blaze.InteractionBridge",
                "");
        }
    }
}
