using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace LiveSplit.ComponentUtil
{
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
            for (Process.ReadPointer(table+(int)(hashcode * 8), out s); s != IntPtr.Zero; Process.ReadPointer(s + 0x10, out s))
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
    }
}
