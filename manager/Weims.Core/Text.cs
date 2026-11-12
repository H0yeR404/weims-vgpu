// SPDX-License-Identifier: GPL-2.0-or-later

namespace Weims.Core;

public static class Text
{
    public static bool Chinese { get; set; } = false;
    public static string Get(string chinese, string english) => Chinese ? chinese : english;
}
