﻿using System;
using System.IO;
using NetVips;

namespace WoWTools.MinimapCut
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 3)
            {
                throw new Exception("Not enough arguments, need inpng, outdir, maxzoom");
            }

            var inpng = args[0];
            var outdir = args[1];
            var maxzoom = int.Parse(args[2]);

            if (!Directory.Exists(outdir))
            {
                Directory.CreateDirectory(outdir);
            }

            var image = Image.NewFromFile(inpng);

            for (var zoom = maxzoom; zoom > 1; zoom--)
            {
                Console.WriteLine(zoom);

                if (zoom != maxzoom)
                {
                    image = image.Resize(0.5, Enums.Kernel.Nearest);
                }

                var width = image.Width;
                var height = image.Height;

                // Always make sure that the image is dividable by 256
                if (width % 256 != 0)
                {
                    width = (width - (width % 256) + 256);
                }

                if (height % 256 != 0)
                {
                    height = (height - (height % 256) + 256);
                }

                image = image.Gravity(Enums.CompassDirection.NorthWest, width, height);

                var w = 0;
                for (var x = 0; x < width; x += 256)
                {
                    var h = 0;
                    for (var y = 0; y < height; y += 256)
                    {
                        image.ExtractArea(x, y, 256, 256).WriteToFile(Path.Combine(outdir, "z" + zoom + "x" + w + "y" + h + ".png"));
                        h++;
                    }
                    w++;
                }
            }
        }
    }
}