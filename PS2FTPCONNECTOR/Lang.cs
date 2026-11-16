using System;
using System.Collections.Generic;
using System.Text;

namespace PS2FTPCONNECTOR
{
    public static class Lang
    {
        public static bool IsDutch { get; set; } = false;

        // Returns Dutch when IsDutch is true, otherwise English
        public static string T(string en, string nl) => IsDutch ? nl : en;
    }
}
