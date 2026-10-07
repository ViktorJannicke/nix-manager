{
  lib,
  stdenv,
  fetchFromGitHub,
}:
stdenv.mkDerivation {
  pname = "webui";
  version = "2.5.0-beta.3";
  src = fetchFromGitHub {
    owner = "webui-dev";
    repo = "webui";
    rev = "fd716ddcac980eee9f8f0c6fb6a94a240343fad0";
    hash = "sha256-RM4GyqSeftjpsQeiGOITWLyxAbeG7kVlTQ3GoC+b4xo=";
  };
  makeFlags = [ "CC=gcc" ];
  installPhase = ''
    runHook preInstall
    install -Dm755 dist/webui-2.so $out/lib/libwebui-2.so
    runHook postInstall
  '';
  meta = {
    description = "Use an installed web browser as an application window";
    homepage = "https://webui.me";
    license = lib.licenses.mit;
    platforms = lib.platforms.linux;
  };
}
