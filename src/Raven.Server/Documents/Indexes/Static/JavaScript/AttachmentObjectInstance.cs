﻿using System;
using System.Collections.Generic;
using System.Text;
using V8.Net;
using Raven.Client.Documents.Indexes;
using Raven.Server.Documents.Patch;
using Raven.Server.Extensions;

namespace Raven.Server.Documents.Indexes.Static.JavaScript
{
    //[ScriptObject("AttachmentObjectInstance", ScriptMemberSecurity.NoAcccess)]
    public class AttachmentObjectInstance : ObjectInstanceBase
    {
        private const string GetContentAsStringMethodName = "getContentAsString";

        private readonly DynamicAttachment _attachment;

        public static InternalHandle CreateObjectBinder(V8EngineEx engine, AttachmentObjectInstance oi, bool keepAlive = false) 
        {
            return engine.CreateObjectBinder<AttachmentObjectInstance.CustomBinder>(oi, engine.TypeBinderAttachmentObjectInstance, keepAlive: keepAlive);
        }

        public AttachmentObjectInstance(DynamicAttachment attachment) : base()
        {
            _attachment = attachment ?? throw new ArgumentNullException(nameof(attachment));
        }

        private InternalHandle GetContentAsString(V8Engine engine, params InternalHandle[] args)
        {
            var encoding = Encoding.UTF8;
            if (args.Length > 0)
            {
                if (args[0].IsStringEx() == false)
                    throw new InvalidOperationException($"Encoding parameter must be of type string and convertible to one of the .NET supported encodings, but was '{args[0]}'.");

                var encodingAsString = args[0].AsString;
                if (string.Equals(encodingAsString, nameof(Encoding.UTF8), StringComparison.OrdinalIgnoreCase))
                    encoding = Encoding.UTF8;
                else if (string.Equals(encodingAsString, nameof(Encoding.Default), StringComparison.OrdinalIgnoreCase))
                    encoding = Encoding.Default;
                else if (string.Equals(encodingAsString, nameof(Encoding.ASCII), StringComparison.OrdinalIgnoreCase))
                    encoding = Encoding.ASCII;
                else if (string.Equals(encodingAsString, nameof(Encoding.BigEndianUnicode), StringComparison.OrdinalIgnoreCase))
                    encoding = Encoding.BigEndianUnicode;
                else if (string.Equals(encodingAsString, nameof(Encoding.Unicode), StringComparison.OrdinalIgnoreCase))
                    encoding = Encoding.Unicode;
                else if (string.Equals(encodingAsString, nameof(Encoding.UTF32), StringComparison.OrdinalIgnoreCase))
                    encoding = Encoding.UTF32;
#pragma warning disable SYSLIB0001 // Type or member is obsolete
                else if (string.Equals(encodingAsString, nameof(Encoding.UTF7), StringComparison.OrdinalIgnoreCase))
                    encoding = Encoding.UTF7;
#pragma warning restore SYSLIB0001 // Type or member is obsolete
                else
                    throw new InvalidOperationException($"Encoding parameter must be of type string and convertible to one of the .NET supported encodings, but was '{encodingAsString}'.");
            }

            return engine.CreateValue(_attachment.GetContentAsString(encoding));
        }

        public override InternalHandle NamedPropertyGetterOnce(V8EngineEx engine, ref string propertyName)
        {
            if (propertyName == nameof(IAttachmentObject.Name))
                return engine.CreateValue(_attachment.Name);
            
            if (propertyName == nameof(IAttachmentObject.ContentType))
                return engine.CreateValue(_attachment.ContentType);
            
            if (propertyName == nameof(IAttachmentObject.Hash))
                return engine.CreateValue(_attachment.Hash);
            
            if (propertyName == nameof(IAttachmentObject.Size))
                return engine.CreateValue(_attachment.Size);
            
            if (propertyName == GetContentAsStringMethodName)
                return engine.CreateCLRCallBack(GetContentAsString, false);

            return InternalHandle.Empty;
        }

        private static InternalHandle GetContentAsString(V8Engine engine, bool isConstructCall, ref InternalHandle self, params InternalHandle[] args) // callback
        {
            try {
                var attachment = (AttachmentObjectInstance)(self.BoundObject);
                if (attachment == null)
                    throw new InvalidOperationException($"GetContentAsString: BoundObject is null.");
                return attachment.GetContentAsString(engine, args);
            }
            catch (Exception e) 
            {
                return engine.CreateError(e.Message, JSValueType.ExecutionError);
            }
        }

        public class CustomBinder : ObjectInstanceBase.CustomBinder<AttachmentObjectInstance>
        {
            public CustomBinder() : base()
            {}
        }

    }
}
