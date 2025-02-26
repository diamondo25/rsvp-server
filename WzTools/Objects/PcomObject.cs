﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WzTools.FileSystem;
using WzTools.Helpers;

namespace WzTools.Objects
{
    public abstract class PcomObject : INameSpaceNode
    {
        public PcomObject Parent = null;

        public abstract ICollection<object> Children { get; }

        public string GetName() => Name;

        public string Name { get; set; }

        public int BlobSize { get; set; }

        public bool IsASCII { get; set; }

        public PcomObject this[string key]
        {
            get => Get(key) as PcomObject;
            set => Set(key, value);
        }

        public static void PrepareEncryption(ArchiveReader reader)
        {
            var start = reader.BaseStream.Position;
            var t = reader.ReadByte();
            if (t == 'A' || t == '#')
            {
                // not needed
            }
            else
            {
                string type = reader.ReadStringWithID(t, 0x1B, 0x73);
                switch (type)
                {
                    // Only a Property is valid on this level
                    case "Property":
                        /*
                    case "List": 
                    case "UOL": 
                    case "Shape2D#Vector2D": 
                    case "Shape2D#Convex2D": 
                    case "Sound_DX8": 
                    case "Canvas": 
                    */
                        break;

                    default:
                        throw new Exception($"Don't know how to read this proptype: {type}");
                }
            }
            reader.BaseStream.Position = start;
        }

        public static PcomObject LoadFromBlob(ArchiveReader reader, int blobSize = 0, string name = null, bool isFileProp = false)
        {
            var start = reader.BaseStream.Position;
            var t = reader.ReadByte();
            var type = "";

            if (t == 'A')
            {
                return null;
            }

            PcomObject obj;
            bool ascii = false;
            if (t == '#')
            {
                blobSize = (int)reader.BaseStream.Length;
                type = reader.ReadAndReturn(() =>
                {
                    // Try to read #Property

                    var text = Encoding.ASCII.GetString(reader.ReadBytes(Math.Min(100, blobSize)));
                    var firstLine = text.Split('\n')[0].Trim();
                    return firstLine;
                });

                reader.BaseStream.Position += type.Length + 2; // \r\n
                ascii = true;
            }
            else
            {
                type = reader.ReadStringWithID(t, 0x1B, 0x73);
            }

            switch (type)
            {
                case "Property":
                    obj = isFileProp ? new WzFileProperty() : new WzProperty();
                    break;
                case "List": obj = new WzList(); break;
                case "UOL": obj = new WzUOL(); break;
                case "Shape2D#Vector2D": obj = new WzVector2D(); break;
                case "Shape2D#Convex2D": obj = new WzConvex2D(); break;
                case "Sound_DX8": obj = new WzSound(); break;
                case "Canvas": obj = new WzCanvas(); break;
                default:
                    Console.WriteLine("Don't know how to read this proptype: {0}", type);
                    return null;
            }

            if (t == '#' && !(obj is WzProperty))
            {
                // Unable to handle non-wzprops???
                return null;
            }

            obj.BlobSize = blobSize - (int)(reader.BaseStream.Position - start);
            obj.Name = name;
            obj.IsASCII = ascii;
            obj.Read(reader);
            return obj;
        }

        public static void WriteToBlob(ArchiveWriter writer, PcomObject obj)
        {
            if (obj is WzProperty prop && prop.IsASCII)
            {
                using var sw = new StreamWriter(writer.BaseStream);
                prop.write_ascii(sw);
                return;
            }

            void WriteType(string type) => writer.Write(type, 0x1B, 0x73);
            switch (obj)
            {
                case WzConvex2D _: WriteType("Shape2D#Convex2D"); break;
                case WzCanvas _: WriteType("Canvas"); break;
                case WzProperty _: WriteType("Property"); break;
                case WzList _: WriteType("List"); break;
                case WzUOL _: WriteType("UOL"); break;
                case WzVector2D _: WriteType("Shape2D#Vector2D"); break;
                case WzSound _: WriteType("Sound_DX8"); break;
                default: throw new NotImplementedException(obj.ToString());
            }

            obj.Write(writer);
        }

        public abstract void Read(ArchiveReader reader);

        public abstract void Write(ArchiveWriter writer);

        public abstract void Set(string key, object value);
        public abstract object Get(string key);

        public virtual bool HasChild(string key) => Get(key) != null;

        public string GetFullPath()
        {
            string ret = Name;
            var curParent = (INameSpaceNode)GetParent();
            while (curParent != null)
            {
                ret = curParent.GetName() + "/" + ret;
                curParent = (INameSpaceNode)curParent.GetParent();
            }

            return ret;
        }

        public override string ToString()
        {
            return base.ToString() + ", Path: " + GetFullPath();
        }

        public abstract void Dispose();

        public virtual object GetParent() => Parent;

        public object GetChild(string key) => Get(key);


        public INameSpaceNode GetNode(string path)
        {
            INameSpaceNode ret = this;
            foreach (string node in path.Trim('/').Split('/'))
            {
                if (string.IsNullOrEmpty(node))
                    break;

                ret = ret?.GetChild(node) as PcomObject;

                if (ret == null)
                    return null;
                if (ret is WzUOL uol)
                    ret = uol.ActualObject(true) as PcomObject;
            }

            if (ret is FSFile file)
                ret = file.Object;

            return ret;
        }

    }
}
