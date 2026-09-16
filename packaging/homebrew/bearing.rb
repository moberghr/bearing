cask "bearing" do
  # Canonical source of the cask. The tap (moberghr/homebrew-bearing) holds a copy at Casks/bearing.rb;
  # edit this one and copy it over — see packaging/homebrew/README.md for the per-release steps.
  #
  # These two are re-pointed per release and must move together — see the url note below.
  version "0.10.3"
  sha256 "dae0c1e1a3cb30c107a55dd1c5620dacf66f1437481dd42fdf9444d6e76382ab"

  # The zip carries no version in its name, so the tag in the URL is what selects the build.
  url "https://github.com/moberghr/bearing/releases/download/v#{version}/BearingSql-osx-Portable.zip"
  name "Bearing"
  desc "Cross-platform SQL query tool and script manager"
  homepage "https://github.com/moberghr/bearing"

  livecheck do
    url :url
    strategy :github_latest
  end

  # Bearing updates itself through Velopack off its own GitHub Releases feed, in place, in /Applications.
  # Without this Homebrew treats a self-updated app as a version mismatch and offers to reinstall over it.
  auto_updates true
  # Apple Silicon only, and that is a property of the release rather than of the app: a Velopack channel
  # holds one package per version and Bearing reads the plain "osx" channel, so an osx-x64 package would
  # replace the arm64 one in the same feed instead of sitting beside it. See build/velopack.sh's header.
  depends_on arch: :arm64
  depends_on macos: :monterey

  app "Bearing.app"

  # Not zapped: the login keychain items holding connection passwords (§1.1). They are the user's
  # credentials, they are shared with any other install, and `brew zap` is not where someone expects to
  # lose them — remove them from Keychain Access if you want them gone.
  zap trash: [
    "~/Library/Application Support/bearing",
    "~/Library/Saved Application State/hr.moberg.bearing.savedState",
  ]

  # The app is unsigned and un-notarized (no Moberg Developer ID). The .NET SDK ad-hoc signs the apphost,
  # which is enough to execute on Apple Silicon, but Gatekeeper refuses a *quarantined* ad-hoc bundle — so
  # this cask must be installed with --no-quarantine until a certificate exists. The caveat says so rather
  # than leaving the user with "Bearing is damaged and can't be opened", which is what it looks like.
  caveats do
    <<~EOS
      Bearing is not signed with an Apple Developer ID yet. If you installed without
      --no-quarantine, macOS will refuse to open it; clear the flag with:

        xattr -dr com.apple.quarantine "#{appdir}/Bearing.app"

      Bearing updates itself from its GitHub Releases feed — `brew upgrade` is not needed.
    EOS
  end
end
