using NixManager.Explain;

namespace NixManager.Tests;

// Output captured from Nix 2.34 and nixpkgs 26.05.
public sealed class ExplainerTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "nix-errors", $"{name}.txt"));

    [Test]
    public async Task DerivationNamesLoseTheirVersion()
    {
        await Assert.That(Explainer.PackageName("steam-1.0.0.87")).IsEqualTo("steam");
        await Assert.That(Explainer.PackageName("ventoy-qt5-1.1.17")).IsEqualTo("ventoy-qt5");
        await Assert.That(Explainer.PackageName("hello")).IsEqualTo("hello");
    }

    [Test]
    public async Task RefusedPackagesSayWhyAndOfferAFixWhereOneExists()
    {
        var unfree = Explainer.Explain(Fixture("unfree"));
        await Assert.That(unfree).IsNotNull();
        await Assert.That(unfree!.Title.English).IsEqualTo("steam is not free software");
        await Assert.That(unfree.Body.English).Contains("Allow non-free software");
        await Assert.That(unfree.Fix).IsNotNull();
        await Assert.That(unfree.Fix!.Kind).IsEqualTo("allow-unfree");
        await Assert.That(unfree.Fix.Packages).IsEquivalentTo(["steam"]);

        var insecure = Explainer.Explain(Fixture("insecure"));
        await Assert.That(insecure).IsNotNull();
        await Assert.That(insecure!.Title.English).IsEqualTo("hello has known security problems");
        await Assert.That(insecure.Body.English).IsEqualTo(
            "Nixpkgs marks hello-2.12.3 as insecure (CVE-1). Choose another package, or allow this version in your configuration if you need it.");
        await Assert.That(insecure.Fix).IsNull();

        await Assert.That(Explainer.Explain(Fixture("broken"))?.Title.English)
            .IsEqualTo("hello does not work in this version of nixpkgs");
        await Assert.That(Explainer.Explain(Fixture("platform"))?.Title.English)
            .IsEqualTo("hello does not run on this computer");
    }

    [Test]
    public async Task RemovedAndMisspelledPackages()
    {
        var aliasThrow = Explainer.Explain(Fixture("aliasthrow"));
        await Assert.That(aliasThrow).IsNotNull();
        await Assert.That(aliasThrow!.Title.English).IsEqualTo("_0verkill is no longer available");
        await Assert.That(aliasThrow.Body.English).IsEqualTo(
            "It was removed from nixpkgs due to lack of maintenance. Remove it from your configuration.");
        await Assert.That(aliasThrow.Fix).IsNull();

        await Assert.That(
                Explainer.Explain("error: '_1password' has been renamed to/replaced by '_1password-cli'")?.Title.English)
            .IsEqualTo("_1password is now called _1password-cli");
        await Assert.That(Explainer.Explain(Fixture("misspelled"))?.Body.English).IsEqualTo(
            "Your configuration refers to exa, which does not exist. Check the spelling; similar names are dxa, ea, era, eta and eva.");
    }

    [Test]
    public async Task OptionProblemsNameTheSettingAndWhereItIsSet()
    {
        await Assert.That(Explainer.Explain(Fixture("nooption"))?.Title.English)
            .IsEqualTo("The setting services.foobarxyz does not exist");

        var type = Explainer.Explain(Fixture("type"));
        await Assert.That(type).IsNotNull();
        await Assert.That(type!.Title.English).IsEqualTo("networking.hostName has a value it does not accept");
        await Assert.That(type.Body.English).IsEqualTo("It expects string in a particular format.");
        await Assert.That(type.Fix).IsNull();

        await Assert.That(Explainer.Explain(Fixture("conflict"))?.Title.English)
            .IsEqualTo("networking.hostName is set to different values");

        var assertion = Explainer.Explain(Fixture("assertion"));
        await Assert.That(assertion).IsNotNull();
        await Assert.That(assertion!.Title.English).IsEqualTo("services.xserver.desktopManager.plasma5 no longer exists");
        await Assert.That(assertion.Body.English).IsEqualTo(
            "The Plasma 5 desktop environment has been removed from nixpkgs, as it has reached EOL upstream. Remove the setting.");
        await Assert.That(assertion.Fix).IsNull();

        await Assert.That(Explainer.Explain(
                "error:\n       Failed assertions:\n       - Swap needs a device.\n       - Users need a\n       password\n")?.Body.English)
            .IsEqualTo("Swap needs a device. Users need a password.");

        const string located =
            "error: The option `a.b' has conflicting definition values:\n" +
            "       - In `/nix/store/0r7xswpqa49y26bl05lydybpw86sywmq-source/hosts/x.nix': 1\n" +
            "       - In `/nix/store/0r7xswpqa49y26bl05lydybpw86sywmq-source/modules/y.nix': 2\n";
        await Assert.That(Explainer.Explain(located)?.Body.English).IsEqualTo(
            "Only one value can apply. It is set in hosts/x.nix and modules/y.nix. Remove all but one of them.");
    }

    [Test]
    public async Task FilesBuildsDisksAndDownloads()
    {
        await Assert.That(Explainer.Explain(Fixture("untracked"))?.Title.English)
            .IsEqualTo("a.nix is not part of the flake yet");
        await Assert.That(Explainer.Explain(Fixture("build"))?.Title.English).IsEqualTo("Building libfoo failed");

        // Message templates from Home Manager's check-link-targets.sh and nixpkgs' buildEnv.
        await Assert.That(Explainer.Explain(
                "Existing file '/home/u/.config/foo' would be clobbered\nExisting file '/home/u/.bashrc' would be clobbered")?.Title.English)
            .IsEqualTo("2 files are in the way");
        await Assert.That(Explainer.Explain(
                "two given paths contain a conflicting subpath:\n  `/nix/store/0r7xswpqa49y26bl05lydybpw86sywmq-vim-9.1/bin/vi' and\n  `/nix/store/1r7xswpqa49y26bl05lydybpw86sywmq-neovim-0.11/bin/vi'\nhint: this may be caused by two different versions")?.Body.English)
            .IsEqualTo("vim and neovim both provide bin/vi. Remove one of them.");
        await Assert.That(Explainer.Explain("error: writing to file: No space left on device")?.Title.English)
            .IsEqualTo("The disk is full");
        await Assert.That(Explainer.Explain(
                "error: unable to download 'https://cache.nixos.org/x': Could not resolve host")?.Title.English)
            .IsEqualTo("A download failed");
        await Assert.That(Explainer.Explain("error: something nobody has seen before")).IsNull();
    }

    [Test]
    public async Task LongInsecurityNotesAreShortenedToTheirFirstSentence()
    {
        await Assert.That(Explainer.Explain(Fixture("insecure-notes"))?.Body.English).IsEqualTo(
            "Nixpkgs marks olm-3.2.16 as insecure (The libolm end‐to‐end encryption library used in many Matrix clients and Jitsi Meet has been deprecated upstream, and relies on a cryptography library that has known side‐channel issues and disclaims that its implementations are not cryptographically secure and should not be used when cryptographic security is required). Choose another package, or allow this version in your configuration if you need it.");
    }
}
