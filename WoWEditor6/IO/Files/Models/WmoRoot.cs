﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WoWEditor6.IO.Files.Models
{
    abstract class WmoRoot
    {
        public abstract Graphics.Texture GetTexture(int index);
    }
}