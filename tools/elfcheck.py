"""Minimal ELF inspector: class, endianness, machine, static/dynamic (PT_INTERP / PT_DYNAMIC).

Usage: python elfcheck.py <file> [<file> ...]
"""
import struct
import sys

MACHINES = {3: "i386", 8: "MIPS", 20: "PPC", 40: "ARM", 62: "x86-64", 183: "AArch64", 243: "RISC-V"}
PT_DYNAMIC, PT_INTERP = 2, 3


def inspect(path: str) -> str:
    data = open(path, "rb").read()
    if data[:4] != b"\x7fELF":
        return "NOT ELF"
    is64 = data[4] == 2
    endian = "<" if data[5] == 1 else ">"
    machine = struct.unpack_from(endian + "H", data, 18)[0]
    if is64:
        phoff = struct.unpack_from(endian + "Q", data, 32)[0]
        phentsize, phnum = struct.unpack_from(endian + "HH", data, 54)
    else:
        phoff = struct.unpack_from(endian + "I", data, 28)[0]
        phentsize, phnum = struct.unpack_from(endian + "HH", data, 42)
    types = [struct.unpack_from(endian + "I", data, phoff + i * phentsize)[0] for i in range(phnum)]
    linkage = "DYNAMIC" if (PT_INTERP in types or PT_DYNAMIC in types) else "STATIC"
    return "{} {}-bit {} {} size={}".format(
        MACHINES.get(machine, machine), 64 if is64 else 32,
        "LE" if endian == "<" else "BE", linkage, len(data))


if __name__ == "__main__":
    for p in sys.argv[1:]:
        print(f"{p}: {inspect(p)}")
