using System;

namespace Magic
{
    /// <summary>
    /// Fallback implementation used when the proprietary BlackMagic DLL is unavailable.
    /// </summary>
    public class BlackMagic
    {
        public BlackMagic(IntPtr processWindowHandle)
        {
        }

        public string ReadASCIIString(int address, int length)
        {
            return string.Empty;
        }

        public byte ReadByte(int address)
        {
            return 0;
        }
    }
}
