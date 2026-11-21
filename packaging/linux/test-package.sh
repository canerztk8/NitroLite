#!/bin/sh
#
# NitroLite — runs inside a clean distribution container, with the packages mounted at /pkg. Installs the package
# with the distribution's own tool (so the declared dependencies are resolved exactly as on a user's machine), checks
# every file is where systemd, polkit and the desktop expect it, starts the program headless, and removes it again.
#
#   docker run --rm -v "$PWD/artifacts:/pkg:ro" -v "$PWD/packaging/linux:/t:ro" ubuntu:24.04 sh /t/test-package.sh deb
#   docker run --rm -v "$PWD/artifacts:/pkg:ro" -v "$PWD/packaging/linux:/t:ro" fedora:41   sh /t/test-package.sh rpm

set -eu
KIND="${1:?usage: test-package.sh deb|rpm}"

case "$KIND" in
    deb)
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq
        apt-get install -y -qq /pkg/*.deb
        ;;
    rpm)
        dnf install -y -q /pkg/*.rpm
        ;;
    *)
        echo "unknown package kind: $KIND" >&2
        exit 2
        ;;
esac

fail() { echo "FAIL: $*" >&2; exit 1; }

test -x /opt/nitrolite/NitroLite.Desktop                                   || fail "the binary is not executable"
test "$(stat -c %U /opt/nitrolite/NitroLite.Desktop)" = root               || fail "the binary is not root-owned"
[ "$(stat -c %a /opt/nitrolite/NitroLite.Desktop)" = 755 ]                 || fail "the binary is writable by others"
test -L /usr/bin/nitrolite                                                 || fail "no nitrolite command"
test -f /usr/lib/systemd/system/nitrolite-helper.socket                    || fail "no helper socket unit"
test -f /usr/lib/systemd/system/nitrolite-helper.service                   || fail "no helper service unit"
grep -q '^ExecStart=/opt/nitrolite/NitroLite.Desktop --linux-helper$' \
    /usr/lib/systemd/system/nitrolite-helper.service                       || fail "the unit runs the wrong binary"
test -f /usr/share/polkit-1/actions/io.github.canerztk8.nitrolite.policy   || fail "no polkit policy"
test -f /usr/lib/udev/rules.d/99-nitrolite.rules                           || fail "no udev rule"
test -f /usr/share/applications/nitrolite.desktop                          || fail "no menu entry"

# Headless start: the hardware report needs no display and no Acer laptop, so it proves the self-contained runtime
# starts and every library it needs (ICU among them) was pulled in by the package's dependencies.
nitrolite --hardware-report > /tmp/report.txt                              || fail "the program did not start"
grep -q 'NitroLite — Linux hardware report' /tmp/report.txt                || fail "no report printed"

# The helper refuses to start without a socket instead of crashing.
code=0
/opt/nitrolite/NitroLite.Desktop --linux-helper 2>/tmp/helper.txt || code=$?
[ "$code" = 2 ]                                                            || fail "the helper exited with $code"

case "$KIND" in
    deb) apt-get remove -y -qq nitrolite ;;
    rpm) dnf remove -y -q nitrolite ;;
esac
test ! -e /opt/nitrolite/NitroLite.Desktop                                 || fail "the binary survived removal"

echo "OK: $KIND installs, runs and removes cleanly"
