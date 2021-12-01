﻿using System;
using Raven.Server.Documents.Patch;
using Raven.Server.ServerWide.Context;
using Raven.Server.SqlMigration.Model;
using Sparrow.Json;
using Raven.Server.Config.Categories;

namespace Raven.Server.SqlMigration
{
    public class JsPatcher : IDisposable
    {
        private readonly ScriptRunner.SingleRun _runner;
        private readonly DocumentsOperationContext _context;
        private ScriptRunner.ReturnRun scriptRunner;
        
        public JsPatcher(IJavaScriptOptions jsOptions, RootCollection collection, DocumentsOperationContext context) 
        {
            if (string.IsNullOrWhiteSpace(collection.Patch)) 
                return;
            
            _context = context;
            var patchRequest = new PatchRequest(collection.Patch, PatchRequestType.None);
            
            scriptRunner = context.DocumentDatabase.Scripts.GetScriptRunner(jsOptions, patchRequest, true, out _runner);
        }

        public BlittableJsonReaderObject Patch(BlittableJsonReaderObject document)
        {
            if (_runner == null)
                return document;

            using (var runner = _runner.Run(_context, _context, "execute", new object[] {document}))
            {
                return runner.TranslateToObject(_context);
            }
        }
        
        public void Dispose()
        {
            scriptRunner.Dispose();
        }
    }
}
