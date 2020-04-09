using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveSplit.ComponentUtil
{
    public enum MonoTypeEnum : byte
    {
        MONO_TYPE_END = 0x00,       /* End of List */
        MONO_TYPE_VOID = 0x01,
        MONO_TYPE_BOOLEAN = 0x02,
        MONO_TYPE_CHAR = 0x03,
        MONO_TYPE_I1 = 0x04,
        MONO_TYPE_U1 = 0x05,
        MONO_TYPE_I2 = 0x06,
        MONO_TYPE_U2 = 0x07,
        MONO_TYPE_I4 = 0x08,
        MONO_TYPE_U4 = 0x09,
        MONO_TYPE_I8 = 0x0a,
        MONO_TYPE_U8 = 0x0b,
        MONO_TYPE_R4 = 0x0c,
        MONO_TYPE_R8 = 0x0d,
        MONO_TYPE_STRING = 0x0e,
        MONO_TYPE_PTR = 0x0f,       /* arg: <type> token */
        MONO_TYPE_BYREF = 0x10,       /* arg: <type> token */
        MONO_TYPE_VALUETYPE = 0x11,       /* arg: <type> token */
        MONO_TYPE_CLASS = 0x12,       /* arg: <type> token */
        MONO_TYPE_VAR = 0x13,      /* number */
        MONO_TYPE_ARRAY = 0x14,       /* type, rank, boundsCount, bound1, loCount, lo1 */
        MONO_TYPE_GENERICINST = 0x15,      /* <type> <type-arg-count> <type-1> \x{2026} <type-n> */
        MONO_TYPE_TYPEDBYREF = 0x16,
        MONO_TYPE_I = 0x18,
        MONO_TYPE_U = 0x19,
        MONO_TYPE_FNPTR = 0x1b,       /* arg: full method signature */
        MONO_TYPE_OBJECT = 0x1c,
        MONO_TYPE_SZARRAY = 0x1d,       /* 0-based one-dim-array */
        MONO_TYPE_MVAR = 0x1e,       /* number */
        MONO_TYPE_CMOD_REQD = 0x1f,       /* arg: typedef or typeref token */
        MONO_TYPE_CMOD_OPT = 0x20,       /* optional arg: typedef or typref token */
        MONO_TYPE_INTERNAL = 0x21,       /* CLR internal type */

        MONO_TYPE_MODIFIER = 0x40,       /* Or with the following types */
        MONO_TYPE_SENTINEL = 0x41,       /* Sentinel for varargs method signature */
        MONO_TYPE_PINNED = 0x45,       /* Local var that points to pinned object */

        MONO_TYPE_ENUM = 0x55        /* an enumeration */
    }

    public class Mono
    {
        /// <summary>
        /// the process containing the mono dll
        /// </summary>
        public readonly Process Process;

        /// <summary>
        /// the mono dll
        /// </summary>
        public readonly ProcessModuleWow64Safe DLL;

        //TODO: look this up based on the mono dll version
        /// <summary>
        /// the offset of the loaded images hash table relative to the base of the mono dll
        /// </summary>
        public const int LOADED_IMAGES_HASH_OFFSET = 0x00494118;

        /// <summary>
        /// the offset of the internal hash table class_cache relative to a MonoImage
        /// </summary>
        public const int CLASS_CACHE_OFFSET = 0x04C0;

        public const int MAX_STRING_SIZE = 2048;

        public const int MONO_CLASS_FIELD_SIZE = 0x20;

        public const ushort FIELD_ATTRIBUTE_STATIC = 0x0010;

        /// <summary>
        /// pointer to hash table containing all currently loaded images
        /// </summary>
        public readonly IntPtr loaded_images_hash;

        public DeepPointer LoadedImagesHashTable;

        /// <summary>
        /// hashing function for strings
        /// </summary>
        /// <remarks>original author Miguel de Icaza, (C) 2006 Novell, Inc.</remarks>
        /// <param name="v1"></param>
        public static uint g_str_hash(string v1)
        {
            uint hash = 0;

            foreach (char p in v1.Substring(1))
            {
                hash = (hash << 5) - (hash + p);
            }

            // The C implementation expects a null-terminated string so we
            // repeat the process one more time at the end.

            hash = (hash << 5) - hash;

            return hash;
        }

        /// <summary>
        /// constructs a new instance of the mono helper
        /// </summary>
        /// <param name="process"></param>
        public Mono(Process process)
        {
            Process = process;

            //get the mono dll currently in use by the process
            DLL = process.ModulesWow64Safe().First(mod => mod.ModuleName.StartsWith("mono"));

            //try to get a pointer to the loaded images hash table
            bool loaded_images_hash_exists = Process.ReadPointer(DLL.BaseAddress + LOADED_IMAGES_HASH_OFFSET, out loaded_images_hash);

            LoadedImagesHashTable = new DeepPointer(DLL.ModuleName, LOADED_IMAGES_HASH_OFFSET, 0x10, 0x00);
        }

        public int GetLoadedImagesHashTableSize()
        {
            return Process.ReadValue<int>(loaded_images_hash + 0x18);
        }

        /// <summary>
        /// reads the loaded images hash table and retrieves a pointer to the specified image
        /// </summary>
        /// <param name="key">the name of the image to find (not including the file path or extension)</param>
        /// <param name="value">a MonoImage* pointing to the requested image or a null pointer if it could not be located</param>
        /// <returns></returns>
        public bool GetImage(string key, out IntPtr value)
        {
            LoadedImagesHashTable.DerefOffsets(Process, out IntPtr table);
            //Debug.WriteLine("using hash table at " + table.ToString("X16"));

            if (loaded_images_hash == IntPtr.Zero)
            {
                value = IntPtr.Zero;
                return false;
            }

            int size = GetLoadedImagesHashTableSize();
            uint hashcode = g_str_hash(key) % (uint)size;
            //Debug.WriteLine("got hash code " + hashcode.ToString());

            IntPtr s, strPtr;
            string str;
            for (Process.ReadPointer(table + (int)(hashcode * 8), out s); s != IntPtr.Zero; Process.ReadPointer(s + 0x10, out s))
            {
                //Debug.WriteLine("looking in bucket at " + s.ToString("X16"));
                strPtr = Process.ReadPointer(s);
                str = Process.ReadNullTerminatedString(strPtr, ReadStringType.ASCII, 2048);
                //Debug.WriteLine("read \"" + str + "\" from " + s.ToString("X16"));
                if (str.Equals(key))
                {
                    Process.ReadPointer(s + 0x08, out value);
                    return true;
                }
            }

            value = IntPtr.Zero;
            return false;
        }

        public bool GetClass(IntPtr image, uint token, out IntPtr klass)
        {
            klass = IntPtr.Zero;
            if ((token & 0xff000000) != 0x02000000)
            {
                //The class much be a type definition, as indicated by the top byte of the token.
                return false;
            }
            IntPtr class_cache = image + CLASS_CACHE_OFFSET;
            int size = Process.ReadValue<int>(class_cache + 0x18);
            Debug.WriteLine("class cache size detected as " + size.ToString());

            IntPtr table = Process.ReadPointer(class_cache + 0x20);
            int bucket = (int)(token % (uint)size);
            //TODO: make this work for 32bit versions too!
            for (IntPtr value = Process.ReadPointer(table + (8 * bucket));
                value != IntPtr.Zero;
                value = Process.ReadPointer(value + 0x0108))
            {
                uint key = Process.ReadValue<uint>(value + 0x58);
                if (key == token)
                {
                    klass = value;
                    return true;
                }
            }

            return false;
        }

        public bool GetVTable(IntPtr klass, out IntPtr vtable, short domain_idx = 0)
        {
            vtable = IntPtr.Zero;
            IntPtr runtime_info = Process.ReadPointer(klass + 0xD0);
            short max_domain = Process.ReadValue<short>(runtime_info + 0x00);

            if (domain_idx >= max_domain)
            {
                return false;
            }

            //TODO: change this so it works with 32bit programs too
            vtable = Process.ReadPointer(runtime_info + 0x08 + (8 * domain_idx));
            return true;
        }

        public bool HasStaticFields(IntPtr vtable)
        {
            return Process.ReadValue<int>(vtable + 0x30) == 0x04;
        }

        public int GetStaticFieldCount(IntPtr klass)
        {
            //TODO: change this so it works with 32bit programs too
            return Process.ReadValue<int>(klass + 0x90) / 8;
        }

        public bool GetClassField(IntPtr klass, int index, out IntPtr field)
        {
            uint field_count = Process.ReadValue<uint>(klass + 0x100);
            if (index < 0 || index >= field_count)
            {
                field = IntPtr.Zero;
                return false;
            }
            if (!Process.ReadPointer(klass + 0x98, out field))
            {
                return false;
            }

            field += MONO_CLASS_FIELD_SIZE * index;
            return true;
        }

        public bool GetFieldAddress(IntPtr obj, IntPtr field, out IntPtr address)
        {
            IntPtr src;

            if (obj == IntPtr.Zero)
            {
                address = IntPtr.Zero;
                return false;
            }

            IntPtr type = Process.ReadPointer(field + 0x00);
            ushort attrs = Process.ReadValue<ushort>(type + 0x08);
            if ((attrs & FIELD_ATTRIBUTE_STATIC) != 0)
            {
                address = IntPtr.Zero;
                return false;
            }

            int offset = Process.ReadValue<int>(field + 0x18);
            src = obj + offset;

            address = src;
            return true;
        }

        private bool VTableGetStaticFieldData(IntPtr vtable, out IntPtr data)
        {
            data = IntPtr.Zero;
            if (!HasStaticFields(vtable))
            {
                return false;
            }

            if (!Process.ReadPointer(vtable, out IntPtr klass))
            {
                return false;
            }

            if (!Process.ReadValue(klass + 0x5C, out int vtable_size))
            {
                return false;
            }

            //TODO: make this 32bit friendly
            data = vtable + 0x40 + (8 * vtable_size);
            return true;
        }

        /// <summary>
        /// Returns the address of a static field of a class.
        /// </summary>
        /// <param name="vtable">
        /// the vtable belonging to the class that owns this field
        /// </param>
        /// <param name="field">
        /// IntPtr to a MonoClassField specifying the (static) class to retrieve
        /// </param>
        /// <param name="address">
        /// the address of the value of the field
        /// </param>
        /// <returns>
        /// a bool indicating success
        /// </returns>
        public bool GetStaticFieldAddress(IntPtr vtable, IntPtr field, out IntPtr address)
        {
            address = IntPtr.Zero;
            IntPtr type = Process.ReadPointer(field + 0x00);
            //attrs is a 16bit bitfield
            ushort attrs = Process.ReadValue<ushort>(type + 0x08);

            //TODO: make these constants
            if ((attrs & FIELD_ATTRIBUTE_STATIC) == 0)
            {
                //This is not a static field.
                return false;
            }

            if ((attrs & 0x40) != 0)
            {
                //This field is a literal.
                throw new NotImplementedException("Reading literals isn't supported yet.");
            }

            int offset = Process.ReadValue<int>(field + 0x18);
            if (offset == -1)
            {
                //This is a special static field.
                throw new NotImplementedException("Reading from special static fields isn't supported yet.");
            }
            else
            {
                VTableGetStaticFieldData(vtable, out IntPtr fielddata);
                address = fielddata + offset;
                return true;
            }
        }
    }
}
