using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenSlideSharp.Interop
{
    /// <summary>
    /// char* - string marshaler.
    /// </summary>
    public class CustomStringMarshaler
        : ICustomMarshaler
    {
        private Encoding _encoding;
        private IntPtr _intPtr = IntPtr.Zero;

        /// <summary>
        /// Get instance.
        /// </summary>
        /// <param name="cookie"><see cref="Encoding.GetEncodings()"/>'s BodyName,default is <see cref="Encoding.Default"/>.BodyName</param>
        /// <returns></returns>
        public static ICustomMarshaler GetInstance(string cookie = "") => new CustomStringMarshaler(string.IsNullOrEmpty(cookie) ? Encoding.Default.BodyName : cookie);

        private CustomStringMarshaler(string encodingName)
        {
            _encoding = Encoding.GetEncoding(encodingName);
        }

        /// <summary>
        /// Marshal managed to native.
        /// </summary>
        /// <param name="managedObj"></param>
        /// <returns></returns>
        public IntPtr MarshalManagedToNative(object managedObj)
        {
            if (managedObj is string str)
            {
                var bytes = new List<byte>(_encoding.GetBytes(str));
                bytes.Add(0); // '\0'
                var ansi = bytes.ToArray();
                _intPtr = Marshal.AllocHGlobal(ansi.Length);
                Marshal.Copy(ansi, 0, _intPtr, ansi.Length);
                return _intPtr;
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// Marshal native to managed.
        /// </summary>
        /// <param name="pNativeData"></param>
        /// <returns></returns>
        public unsafe object MarshalNativeToManaged(IntPtr pNativeData)
        {
            sbyte* str = (sbyte*)pNativeData;
            if (str == null) return null;
            for (int arrayLength = 0; ; arrayLength++)
            {
                if (str[arrayLength] == 0)
                    return new string(str, 0, arrayLength, _encoding);
            }
        }

        /// <summary>
        /// Clean up native data.
        /// </summary>
        /// <param name="pNativeData"></param>
        public void CleanUpNativeData(IntPtr pNativeData)
        {
            Marshal.FreeHGlobal(_intPtr);
        }

        /// <summary>
        /// Clean up managed data.
        /// </summary>
        /// <param name="managedObj"></param>
        public virtual void CleanUpManagedData(object managedObj) => throw new NotImplementedException();

        /// <summary>
        /// Get native data size.
        /// </summary>
        /// <returns></returns>
        public virtual int GetNativeDataSize() => throw new NotImplementedException();
    }
}
