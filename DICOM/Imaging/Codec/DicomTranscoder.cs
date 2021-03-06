﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.ComponentModel.Composition.Hosting;

using NLog;

namespace Dicom.Imaging.Codec {
	public class DicomTranscoder {
		#region Static
		private static Dictionary<DicomTransferSyntax,IDicomCodec> _codecs = new Dictionary<DicomTransferSyntax,IDicomCodec>();

		static DicomTranscoder() {
			LoadCodecs(null, "Dicom.Native*.dll");
		}

		public static IDicomCodec GetCodec(DicomTransferSyntax syntax) {
			IDicomCodec codec = null;
			if (!_codecs.TryGetValue(syntax, out codec))
				throw new DicomCodecException("No codec registered for tranfer syntax: {0}", syntax);
			return codec;
		}

		public static void LoadCodecs(string path = null, string search = null) {
			if (path == null)
				path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

			var log = LogManager.GetLogger("Dicom.Imaging.Codec");

			var catalog = (search == null) ?
				new DirectoryCatalog(path) :
				new DirectoryCatalog(path, search);
			var container = new CompositionContainer(catalog);
			foreach (var lazy in container.GetExports<IDicomCodec>()) {
				var codec = lazy.Value;
				log.Debug("Codec: {0}", codec.TransferSyntax.UID.Name);
				_codecs[codec.TransferSyntax] = codec;
			}
		}
		#endregion

		public DicomTranscoder(DicomTransferSyntax input, DicomTransferSyntax output) {
			InputSyntax = input;
			OutputSyntax = output;
		}

		public DicomTransferSyntax InputSyntax {
			get;
			private set;
		}

		public DicomCodecParams InputCodecParams {
			get;
			set;
		}

		private IDicomCodec _inputCodec;
		private IDicomCodec InputCodec {
			get {
				if (InputSyntax.IsEncapsulated && _inputCodec == null)
					_inputCodec = GetCodec(InputSyntax);
				return _inputCodec;
			}
		}

		public DicomTransferSyntax OutputSyntax {
			get;
			private set;
		}

		public DicomCodecParams OutputCodecParams {
			get;
			set;
		}

		private IDicomCodec _outputCodec;
		private IDicomCodec OutputCodec {
			get {
				if (OutputSyntax.IsEncapsulated && _outputCodec == null)
					_outputCodec = GetCodec(OutputSyntax);
				return _outputCodec;
			}
		}

		public DicomFile Transcode(DicomFile file) {
			DicomFile f = new DicomFile();
			f.FileMetaInfo.Add(file.FileMetaInfo);
			f.FileMetaInfo.TransferSyntax = OutputSyntax;
			f.Dataset.Add(Transcode(file.Dataset));
			return f;
		}

		public DicomDataset Transcode(DicomDataset dataset) {
			if (InputSyntax.IsEncapsulated && OutputSyntax.IsEncapsulated) {
				DicomDataset temp = Decode(dataset, DicomTransferSyntax.ExplicitVRLittleEndian, InputCodec, InputCodecParams);
				return Encode(temp, OutputSyntax, OutputCodec, OutputCodecParams);
			}

			if (InputSyntax.IsEncapsulated)
				return Decode(dataset, OutputSyntax, InputCodec, InputCodecParams);

			if (OutputSyntax.IsEncapsulated)
				return Encode(dataset, OutputSyntax, OutputCodec, OutputCodecParams);

			return dataset.Clone();
		}

		private DicomDataset Decode(DicomDataset oldDataset, DicomTransferSyntax outSyntax, IDicomCodec codec, DicomCodecParams parameters) {
			DicomPixelData oldPixelData = DicomPixelData.Create(oldDataset, false);

			DicomDataset newDataset = oldDataset.Clone();
			newDataset.InternalTransferSyntax = outSyntax;
			DicomPixelData newPixelData = DicomPixelData.Create(newDataset, true);

			codec.Decode(oldPixelData, newPixelData, parameters);

			return newDataset;
		}

		private DicomDataset Encode(DicomDataset oldDataset, DicomTransferSyntax inSyntax, IDicomCodec codec, DicomCodecParams parameters) {
			DicomPixelData oldPixelData = DicomPixelData.Create(oldDataset, false);

			DicomDataset newDataset = oldDataset.Clone();
			newDataset.InternalTransferSyntax = codec.TransferSyntax;
			DicomPixelData newPixelData = DicomPixelData.Create(newDataset, true);

			codec.Encode(oldPixelData, newPixelData, parameters);

			if (codec.TransferSyntax.IsLossy && newPixelData.NumberOfFrames > 0) {
				newDataset.Add(new DicomCodeString(DicomTag.LossyImageCompression, "01"));

				List<string> methods = new List<string>();
				if (newDataset.Exists(DicomTag.LossyImageCompressionMethod))
					methods.AddRange(newDataset.Get<string[]>(DicomTag.LossyImageCompressionMethod));
				methods.Add(codec.TransferSyntax.LossyCompressionMethod);
				newDataset.Add(new DicomCodeString(DicomTag.LossyImageCompressionMethod, methods.ToArray()));

				double oldSize = oldPixelData.GetFrame(0).Size;
				double newSize = newPixelData.GetFrame(0).Size;
				string ratio = String.Format("{0:0.000}", oldSize / newSize);
				newDataset.Add(new DicomDecimalString(DicomTag.LossyImageCompressionRatio, ratio));
			}

			return newDataset;
		}
	}
}
