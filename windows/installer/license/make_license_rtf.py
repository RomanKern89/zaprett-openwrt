"""Generates the licence pages of the installer: windows/installer/License.<culture>.rtf (ru-RU, en-US, zh-CN).

The MIT licence itself is quoted in English (the legally binding text); each page adds a short summary and the
third-party notes in its own language. Non-ASCII characters are written as RTF \\uN escapes, so the files are plain
ASCII and do not depend on the MSI code page.

Run after editing the texts below:
    PYTHONUTF8=1 python windows/installer/license/make_license_rtf.py
"""
import os
import sys

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

OUT_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

MIT = [
    "Copyright (c) 2026 zaprett-openwrt contributors",
    "",
    "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated "
    "documentation files (the \"Software\"), to deal in the Software without restriction, including without "
    "limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the "
    "Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:",
    "",
    "The above copyright notice and this permission notice shall be included in all copies or substantial portions "
    "of the Software.",
    "",
    "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED "
    "TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE "
    "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF "
    "CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER "
    "DEALINGS IN THE SOFTWARE.",
]

PAGES = {
    "en-US": {
        "font": "Tahoma",
        "title": "MIT License",
        "summary": [],
        "third_title": "Third-party components",
        "third": [
            "zapret and zapret2 (winws.exe, winws2.exe, Lua scripts) by bol-van, MIT License. WinDivert by basil00, "
            "LGPL-3.0 / GPL-2.0 (driver and library shipped unmodified). Cygwin runtime (cygwin1.dll), LGPL-3.0. "
            "The .NET runtime and the Windows App SDK, MIT License, (c) Microsoft Corporation. Strategies and lists "
            "keep the terms of their sources (see the project README).",
        ],
    },
    "ru-RU": {
        "font": "Tahoma",
        "title": "Лицензия MIT",
        "summary": [
            "zaprett распространяется по лицензии MIT. Кратко: программу можно бесплатно использовать, изменять и "
            "распространять, сохраняя уведомление об авторских правах; она предоставляется «как есть», без каких-либо "
            "гарантий. Ниже — юридически значимый текст лицензии на английском языке.",
        ],
        "third_title": "Сторонние компоненты",
        "third": [
            "zapret и zapret2 (winws.exe, winws2.exe, скрипты Lua), автор bol-van, лицензия MIT. WinDivert, автор "
            "basil00, LGPL-3.0 / GPL-2.0 (драйвер и библиотека поставляются без изменений). Среда Cygwin "
            "(cygwin1.dll), LGPL-3.0. Среда .NET и Windows App SDK, лицензия MIT, (c) Microsoft Corporation. "
            "Стратегии и списки сохраняют условия своих источников (см. README проекта).",
        ],
    },
    "zh-CN": {
        "font": "Microsoft YaHei",
        "title": "MIT 许可证",
        "summary": [
            "zaprett 按 MIT 许可证发布。简而言之：您可以免费使用、修改和分发本软件，但须保留版权声明；本软件按“原样”"
            "提供，不附带任何形式的担保。以下为具有法律效力的英文许可证原文。",
        ],
        "third_title": "第三方组件",
        "third": [
            "zapret 和 zapret2（winws.exe、winws2.exe、Lua 脚本），作者 bol-van，MIT 许可证。WinDivert，作者 basil00，"
            "LGPL-3.0 / GPL-2.0（驱动程序和库未经修改）。Cygwin 运行库（cygwin1.dll），LGPL-3.0。.NET 运行时和 "
            "Windows App SDK，MIT 许可证，(c) Microsoft Corporation。策略和列表沿用其来源的条款（见项目 README）。",
        ],
    },
}


def rtf_text(text: str) -> str:
    out = []
    for ch in text:
        code = ord(ch)
        if ch in "\\{}":
            out.append("\\" + ch)
        elif 32 <= code < 128:
            out.append(ch)
        else:
            if code > 0x7FFF:
                code -= 0x10000
            out.append("\\u%d?" % code)
    return "".join(out)


def page(culture: str) -> str:
    p = PAGES[culture]
    lines = ["{\\rtf1\\ansi\\ansicpg1252\\uc1\\deff0{\\fonttbl{\\f0\\fnil %s;}}\\fs16" % p["font"]]
    lines.append("\\b " + rtf_text(p["title"]) + "\\b0\\par")
    lines.append("\\par")
    for para in p["summary"]:
        lines.append(rtf_text(para) + "\\par")
        lines.append("\\par")
    if culture != "en-US":
        lines.append("\\b MIT License\\b0\\par")
        lines.append("\\par")
    for para in MIT:
        lines.append(rtf_text(para) + "\\par")
    lines.append("\\par")
    lines.append("\\b " + rtf_text(p["third_title"]) + "\\b0\\par")
    for para in p["third"]:
        lines.append(rtf_text(para) + "\\par")
    lines.append("}")
    return "\n".join(lines) + "\n"


def main() -> None:
    for culture in PAGES:
        data = page(culture).encode("ascii")
        path = os.path.join(OUT_DIR, "License.%s.rtf" % culture)
        with open(path, "wb") as fh:
            fh.write(data)
        print("%s  %d bytes" % (path, len(data)))


if __name__ == "__main__":
    main()
