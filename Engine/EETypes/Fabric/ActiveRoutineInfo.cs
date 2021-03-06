﻿using Dasync.EETypes.Descriptors;

namespace Dasync.EETypes.Fabric
{
    public class ActiveRoutineInfo
    {
        public string RoutineId;

        public string ETag;

        /// <summary>
        /// Not null when routine is completed.
        /// </summary>
        public TaskResult Result;
    }
}
