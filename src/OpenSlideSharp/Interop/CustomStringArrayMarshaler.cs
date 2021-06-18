using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenSlideSharp.Interop
{

    /// <summary>
    /// char** - string[] marshaler
    /// </summary>
    public class CustomStringArrayMarshaler
        : ICustomMarshaler
    {
        private ICustomMarshaler _encoding;
        private List<IntPtr> _intPtrs = new List<IntPtr>();

        /// <summary>
        /// Get instance.
        /// </summary>
        /// <param name="cookie"><see cref="Encoding.GetEncodings()"/>'s BodyName,default is <see cref="Encoding.Default"/>.BodyName</param>
        /// <returns></returns>
        public static ICustomMarshaler GetInstance(string cookie = "") => new CustomStringArrayMarshaler(string.IsNullOrEmpty(cookie) ? Encoding.Default.BodyName : cookie);

        private CustomStringArrayMarshaler(string encodingName)
        {
            _encoding = CustomStringMarshaler.GetInstance(encodingName);
        }

        /// <summary>
        /// Marshal managed to native.
        /// </summary>
        /// <param name="managedObj"></param>
        /// <returns></returns>
        public unsafe IntPtr MarshalManagedToNative(object managedObj)
        {
            if (managedObj is string[] strArray)
            {
                _intPtrs.Add(Marshal.AllocHGlobal((strArray.Length + 1) * IntPtr.Size));
                for (int i = 0; i < strArray.Length; i++)
                {
                    IntPtr pNative = _encoding.MarshalManagedToNative(strArray[i]);
                    _intPtrs.Add(pNative);
                    Marshal.WriteIntPtr((IntPtr)((byte*)_intPtrs[0] + IntPtr.Size * i), pNative);
                }
                Marshal.WriteIntPtr((IntPtr)((byte*)_intPtrs[0] + IntPtr.Size * strArray.Length), IntPtr.Zero);
                return _intPtrs[0];
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
            byte** arrayPtr = (byte**)pNativeData;
            if (arrayPtr == null) return null;
            var output = new List<string>();
            for (int arrayLength = 0; ; arrayLength++)
            {
                if (arrayPtr[arrayLength] != null)
                    output.Add(_encoding.MarshalNativeToManaged((IntPtr)arrayPtr[arrayLength]) as string);
                else
                    return output.ToArray();
            }
        }

        /// <summary>
        /// Clean up native data.
        /// </summary>
        /// <param name="pNativeData"></param>
        public void CleanUpNativeData(IntPtr pNativeData)
        {
            if (_intPtrs.Count > 0)
            {
                for (int i = _intPtrs.Count - 1; i > 0; i--)
                {
                    _encoding.CleanUpNativeData(_intPtrs[i]);
                }
                Marshal.FreeHGlobal(_intPtrs[0]);
            }
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
