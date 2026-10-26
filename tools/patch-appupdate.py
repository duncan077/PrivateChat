#!/usr/bin/env python3
"""Replace the AppUpdate section of an appsettings.json in place.

Used by tools/publish-release.ps1 on both sides of a deploy:

  * locally, so the committed appsettings.json records the release
  * on the server, so production secrets in the rest of the file survive

Running the same code in both places keeps the two copies byte-identical in
formatting, which matters because the local file is committed - a whole-file
reformat on every release buries the one line that actually changed.

Usage:
    patch-appupdate.py <appsettings.json> <appupdate-patch.json> [--backup]
"""

import argparse
import datetime
import json
import os
import shutil
import sys


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("settings", help="appsettings.json to patch in place")
    parser.add_argument("patch", help="JSON file holding the new AppUpdate object")
    parser.add_argument(
        "--backup",
        action="store_true",
        help="write a timestamped .bak copy first (used on the server)",
    )
    args = parser.parse_args()

    if not os.path.isfile(args.settings):
        print(f"error: {args.settings} not found", file=sys.stderr)
        return 1
    if not os.path.isfile(args.patch):
        print(f"error: {args.patch} not found", file=sys.stderr)
        return 1

    # utf-8-sig tolerates a BOM, which Windows editors like to add.
    with open(args.settings, encoding="utf-8-sig") as handle:
        settings = json.load(handle)
    with open(args.patch, encoding="utf-8-sig") as handle:
        settings["AppUpdate"] = json.load(handle)

    if args.backup:
        stamp = datetime.datetime.now().strftime("%Y%m%d%H%M%S")
        shutil.copy2(args.settings, f"{args.settings}.bak.{stamp}")

    # 2-space indent and LF endings match how the file is already written, so the
    # diff stays limited to the AppUpdate block. newline="" stops Python from
    # translating "\n" to "\r\n" on Windows.
    with open(args.settings, "w", encoding="utf-8", newline="") as handle:
        json.dump(settings, handle, indent=2, ensure_ascii=False)
        handle.write("\n")

    print(f"AppUpdate -> {settings['AppUpdate'].get('LatestVersion', '?')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
