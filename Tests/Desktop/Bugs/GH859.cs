﻿// Copyright (c) 2012-2019 fo-dicom contributors.
// Licensed under the Microsoft Public License (MS-PL).



using Dicom.Helpers;
using Dicom.Network;

using System.Threading;
using System.Threading.Tasks;

using Xunit;
using Xunit.Abstractions;

using DicomClient = Dicom.Network.Client.DicomClient;

namespace Dicom.Bugs
{
    public class GH859
    {
        private readonly XUnitDicomLogger _output;

        public GH859(ITestOutputHelper output)
        {
            _output = new XUnitDicomLogger(output)
                .IncludeTimestamps()
                .IncludeThreadId()
                .IncludePrefix("GH859");
        }

        [Fact]
        public async Task DicomService_reading_messages_with_invalid_UIDs_does_not_fail()
        {
            int port = Ports.GetNext();
            var clientLogger = _output.IncludePrefix(nameof(Network.DicomClient));
            var serverLogger = _output.IncludePrefix(nameof(DicomCEchoProvider));
            var source = new CancellationTokenSource();

            using (var server = DicomServer.Create<SimpleCStoreProvider>(port,
                logger: serverLogger,
                options: new DicomServiceOptions
                {
                    LogDataPDUs = true,
                    LogDimseDatasets = true
                }))
            {
                while (!server.IsListening)
                    await Task.Delay(50);

                var client = new DicomClient("127.0.0.1", port, false, "SCU", "ANY-SCP");
                client.Logger = clientLogger;

                var command = new DicomDataset();
                command.ValidateItems = false;
                command.Add(DicomTag.CommandField, (ushort)DicomCommandField.CStoreRequest);
                command.Add(DicomTag.MessageID, (ushort)1);
                command.Add(DicomTag.AffectedSOPClassUID, DicomUID.CTImageStorage);
                command.Add(new DicomUniqueIdentifier(DicomTag.AffectedSOPInstanceUID, "1.2.3.04"));

                var request = new DicomCStoreRequest(command);
                request.File = new DicomFile();
                request.Dataset = new DicomDataset();
                request.Dataset.ValidateItems = false;
                request.Dataset.Add(DicomTag.SOPClassUID, DicomUID.CTImageStorage);
                request.Dataset.Add(new DicomUniqueIdentifier(DicomTag.SOPInstanceUID, "1.2.3.04"));

                request.OnResponseReceived += (e, args) =>
                {
                    _output.Info("Response received. Cancelling in 500ms.");
                    source.CancelAfter(100);
                };

                client.AddRequest(request);

                await client.SendAsync(source.Token);
            }
        }
    }
}
