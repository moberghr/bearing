cask "bearing" do
  # Canonical source of the cask. The tap (moberghr/homebrew-bearing) holds a copy at Casks/bearing.rb;
  # edit this one and copy it over — see packaging/homebrew/README.md for the per-release steps.
  #
  # These two are re-pointed per release and must move together — see the url note below.
  version "1.0.0"
  sha256 "9ab4dd24ec2c45d59787db5e961a44a69398d5c06a30c3c8471e400b016cc0fc"

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

  # The `bearing` command ships inside the bundle (build/velopack.sh publishes it beside the GUI apphost)
  # and is put on PATH here, which is the only step that makes it a command rather than a file. `bearing`
  # with no arguments opens the app, so this is the whole entry point — the GUI apphost beside it is called
  # `bearing-app` precisely so this one can have the name.
  binary "#{appdir}/Bearing.app/Contents/MacOS/bearing"

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
      IF MACOS SAYS "Bearing is damaged and can't be opened" — it is not damaged. That is
      Gatekeeper refusing an unsigned app that carries the quarantine flag. Clear it:

        xattr -dr com.apple.quarantine "#{appdir}/Bearing.app"

      To avoid it next time, install with --no-quarantine, which Homebrew accepts only on the
      command line and a cask cannot set for you:

        brew install --cask --no-quarantine moberghr/bearing/bearing

      Bearing has no Apple Developer ID yet, which is why either step is needed at all.

      Bearing updates itself from its GitHub Releases feed — `brew upgrade` is not needed.
    EOS
  end
end
