#!/bin/sh
# Builds the native library into native/lib for the current OS (Linux / macOS / MSYS-MinGW).
set -e
cd "$(dirname "$0")"
mkdir -p lib
case "$(uname -s)" in
  Darwin)               cc -shared -fPIC -O2 -o lib/libdqnative.dylib dqnative.c ;;
  MINGW*|MSYS*|CYGWIN*) gcc -shared -O2 -o lib/dqnative.dll dqnative.c ;;
  *)                    cc -shared -fPIC -O2 -o lib/libdqnative.so dqnative.c ;;
esac
echo "Native library built in native/lib"
