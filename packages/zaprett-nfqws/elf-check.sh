#!/bin/sh
# Refuse to package a binary built for another CPU.
# Usage: elf-check.sh <file> "<class> <data> <machine>"
#   class: 1 = 32-bit, 2 = 64-bit; data: 1 = little endian, 2 = big endian; machine: ELF e_machine number.
set -eu

file="$1"
expected="$2"

[ -f "$file" ] || { echo "elf-check: $file not found" >&2; exit 1; }

byte() {
	od -An -tu1 -j "$2" -N 1 "$1" | tr -d ' '
}

[ "$(od -An -c -j 1 -N 3 "$file" | tr -d ' ')" = "ELF" ] || { echo "elf-check: $file is not an ELF file" >&2; exit 1; }

class=$(byte "$file" 4)
data=$(byte "$file" 5)
lo=$(byte "$file" 18)
hi=$(byte "$file" 19)
if [ "$data" = 1 ]; then
	machine=$((lo + hi * 256))
else
	machine=$((lo * 256 + hi))
fi

actual="$class $data $machine"
if [ "$actual" != "$expected" ]; then
	echo "elf-check: $file is ELF '$actual', expected '$expected'" >&2
	exit 1
fi
echo "elf-check: $file OK ($actual)"
