using System;
using System.Collections.Generic;
using System.Text;

namespace Mellow.SlopFactory.Core
{
    public class ComfyApiSettings
    {
        public string? BaseUrl { get; set; }
        public string? ApiKey { get; set; }

        /// <summary>
        /// Whether BaseUrl points at Comfy Cloud rather than a self-hosted ComfyUI server.
        /// Leave null to detect it from the host name (cloud.comfy.org).
        /// </summary>
        public bool? IsCloud { get; set; }
    }
}
