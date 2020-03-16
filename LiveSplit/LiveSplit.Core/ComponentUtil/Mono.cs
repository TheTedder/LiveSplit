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
        public const int LOADED_IMAGES_HASH_OFFSET = 0x494108;

        /// <summary>
        /// pointer to hash table containing all currently loaded images
        /// </summary>
        public readonly IntPtr loaded_images_hash;
        
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
        }
    }
}
