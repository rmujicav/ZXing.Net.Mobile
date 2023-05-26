using System;
using System.Collections.Generic;
using System.Linq;
using AVFoundation;
using CoreFoundation;
using CoreGraphics;
using CoreMedia;
using Foundation;
using ObjCRuntime;
using UIKit;

namespace ZXing.Mobile
{
    public class AVCaptureScannerView : UIView, IZXingScanner<UIView>, IScannerSessionHost
    {
        public AVCaptureScannerView()
        {
        }

        public AVCaptureScannerView(IntPtr handle) : base(handle)
        {
        }

        public AVCaptureScannerView(CGRect frame) : base(frame)
        {
        }

        AVCaptureSession session;
        AVCaptureVideoPreviewLayer previewLayer;
        //AVCaptureVideoDataOutput output;
        //OutputRecorder outputRecorder;
        //DispatchQueue queue;
        Action<ZXing.Result> resultCallback;
        volatile bool stopped = true;

        volatile bool foundResult = false;
        CaptureDelegate captureDelegate;

        UIView layerView;
        UIView overlayView = null;

        public event Action OnCancelButtonPressed;

        public string CancelButtonText { get; set; }
        public string FlashButtonText { get; set; }

        public MobileBarcodeScanningOptions ScanningOptions { get; set; }

        void Setup()
        {
            if (overlayView != null)
                overlayView.RemoveFromSuperview();

            if (UseCustomOverlayView && CustomOverlayView != null)
                overlayView = CustomOverlayView;
            else
            {
                overlayView = new ZXingDefaultOverlayView(new CGRect(0, 0, this.Frame.Width, this.Frame.Height),
                    TopText, BottomText, CancelButtonText, FlashButtonText,
                    () => OnCancelButtonPressed?.Invoke(), ToggleTorch);
            }

            if (overlayView != null)
            {
                overlayView.Frame = new CGRect(0, 0, this.Frame.Width, this.Frame.Height);
                overlayView.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
            }
        }


        bool torch = false;
        bool analyzing = true;
        DateTime lastAnalysis = DateTime.UtcNow.AddYears(-99);
        bool wasScanned = false;
        bool working = false;


        bool SetupCaptureSession()
        {
            var availableResolutions = new List<CameraResolution>();

            var consideredResolutions = new Dictionary<NSString, CameraResolution> {
                { AVCaptureSession.Preset352x288, new CameraResolution   { Width = 352,  Height = 288 } },
                { AVCaptureSession.PresetMedium, new CameraResolution    { Width = 480,  Height = 360 } }, //480x360
				{ AVCaptureSession.Preset640x480, new CameraResolution   { Width = 640,  Height = 480 } },
                { AVCaptureSession.Preset1280x720, new CameraResolution  { Width = 1280, Height = 720 } },
                { AVCaptureSession.Preset1920x1080, new CameraResolution { Width = 1920, Height = 1080 } },
                { AVCaptureSession.Preset3840x2160, new CameraResolution { Width = 3840, Height = 2160 } }
            };

            // configure the capture session for low resolution, change this if your code
            // can cope with more data or volume
            session = new AVCaptureSession()
            {
                SessionPreset = PresetConverter.ToAVCaptureSessionPreset(ScanningOptions.CameraResolutionPreset)
            };

            // create a device input and attach it to the session
            //			var captureDevice = AVCaptureDevice.DefaultDeviceWithMediaType (AVMediaType.Video);			

            var TryUseUltraWideCamera = ScanningOptions.TryUseUltraWideCamera.HasValue && ScanningOptions.TryUseUltraWideCamera.Value;
            var useFrontCameraIfAvailable = ScanningOptions.UseFrontCameraIfAvailable.HasValue && ScanningOptions.UseFrontCameraIfAvailable.Value;

            var specificDevices = AVCaptureDeviceDiscoverySession.Create(new AVCaptureDeviceType[] {
                AVCaptureDeviceType.BuiltInWideAngleCamera,
                AVCaptureDeviceType.BuiltInUltraWideCamera,
                AVCaptureDeviceType.BuiltInTripleCamera,
                AVCaptureDeviceType.BuiltInDualWideCamera,
                AVCaptureDeviceType.BuiltInDuoCamera,
                AVCaptureDeviceType.BuiltInTelephotoCamera
            }, AVMediaType.Video, AVCaptureDevicePosition.Back)?.Devices?.ToList() ?? new List<AVCaptureDevice>();

            AVCaptureDevice defaultDevice = null;
            var defaultDevices = AVCaptureDevice.DevicesWithMediaType(AVMediaType.Video).ToList();
            if (useFrontCameraIfAvailable)
                defaultDevice = defaultDevices.FirstOrDefault(x => x.Position == AVCaptureDevicePosition.Front);
            if (defaultDevice == null)
                defaultDevice = defaultDevices.FirstOrDefault(x => x.Position == AVCaptureDevicePosition.Back);


            var devices = new List<AVCaptureDevice>();
            devices.AddRange(specificDevices);
            if (!devices.Contains(defaultDevice))
                devices = devices.Prepend(defaultDevice).ToList();

            // create a device input and attach it to the session                     

            AVCaptureDevice captureDevice = null;
            devices = devices.Where(x => x.Position == (useFrontCameraIfAvailable ? AVCaptureDevicePosition.Front : AVCaptureDevicePosition.Back)).ToList();

            /**/
            if (TryUseUltraWideCamera)
                captureDevice = devices.FirstOrDefault(x => x.DeviceType == AVCaptureDeviceType.BuiltInUltraWideCamera);

            if (captureDevice == null)
                captureDevice = devices.FirstOrDefault(); /**/

            //captureDevice = GetBestAvailableBackFacingCamera();
            //DeviceAutomaticVideoZoomFactor.ApplyAutomaticZoomFactorIfNeeded(captureDevice, 20);

            Console.WriteLine($"captureDevice.DeviceType: {captureDevice.DeviceType}");

            if (captureDevice == null)
            {
                Console.WriteLine("No captureDevice - this won't work on the simulator, try a physical device");
                if (overlayView != null)
                {
                    AddSubview(overlayView);
                    BringSubviewToFront(overlayView);
                }
                return false;
            }

            CameraResolution resolution = null;

            // Find resolution
            // Go through the resolutions we can even consider
            foreach (var cr in consideredResolutions)
            {
                // Now check to make sure our selected device supports the resolution
                // so we can add it to the list to pick from
                if (captureDevice.SupportsAVCaptureSessionPreset(cr.Key))
                    availableResolutions.Add(cr.Value);
            }

            resolution = ScanningOptions.GetResolution(availableResolutions);

            // See if the user selected a resolution
            if (resolution != null)
            {
                // Now get the preset string from the resolution chosen
                var preset = (from c in consideredResolutions
                              where c.Value.Width == resolution.Width
                              && c.Value.Height == resolution.Height
                              select c.Key).FirstOrDefault();

                // If we found a matching preset, let's set it on the session
                if (!string.IsNullOrEmpty(preset))
                    session.SessionPreset = preset;
            }

            var input = AVCaptureDeviceInput.FromDevice(captureDevice);
            if (input == null)
            {
                Console.WriteLine("No input - this won't work on the simulator, try a physical device");
                if (overlayView != null)
                {
                    AddSubview(overlayView);
                    BringSubviewToFront(overlayView);
                }
                return false;
            }
            else
                session.AddInput(input);


            foundResult = false;
            //Detect barcodes with built in avcapture stuff
            var metadataOutput = new AVCaptureMetadataOutput();

            captureDelegate = new CaptureDelegate(metaDataObjects =>
            {
                if (!analyzing)
                    return;

                var msSinceLastPreview = (DateTime.UtcNow - lastAnalysis).TotalMilliseconds;

                if (msSinceLastPreview < ScanningOptions.DelayBetweenAnalyzingFrames
                    || (wasScanned && msSinceLastPreview < ScanningOptions.DelayBetweenContinuousScans)
                    || working)
                    return;

                try
                {
                    working = true;
                    wasScanned = false;
                    lastAnalysis = DateTime.UtcNow;

                    var mdo = metaDataObjects.FirstOrDefault();

                    if (!(mdo is AVMetadataMachineReadableCodeObject readableObj))
                        return;

                    if (readableObj.Type == AVMetadataObjectType.CatBody
                        || readableObj.Type == AVMetadataObjectType.DogBody
                        || readableObj.Type == AVMetadataObjectType.Face
                        || readableObj.Type == AVMetadataObjectType.HumanBody
                        || readableObj.Type == AVMetadataObjectType.SalientObject)
                        return;

                    wasScanned = true;

                    var zxingFormat = ZXingBarcodeFormatFromAVCaptureBarcodeFormat(readableObj.Type.ToString());

                    var rs = new ZXing.Result(readableObj.StringValue, null, null, zxingFormat);

                    resultCallback(rs);
                }
                finally
                {
                    working = false;
                }
            });

            metadataOutput.SetDelegate(captureDelegate, DispatchQueue.MainQueue);
            session.AddOutput(metadataOutput);

            //Setup barcode formats
            if (ScanningOptions?.PossibleFormats?.Any() ?? false)
            {
                var formats = AVMetadataObjectType.None;

                foreach (var f in ScanningOptions.PossibleFormats)
                    formats |= AVCaptureBarcodeFormatFromZXingBarcodeFormat(f);

                formats &= ~AVMetadataObjectType.None;

                metadataOutput.MetadataObjectTypes = formats;
            }
            else
            {
                var availableMetaObjTypes = metadataOutput.AvailableMetadataObjectTypes;


                metadataOutput.MetadataObjectTypes = metadataOutput.AvailableMetadataObjectTypes;


            }

            previewLayer = new AVCaptureVideoPreviewLayer(session);
            previewLayer.VideoGravity = AVLayerVideoGravity.ResizeAspectFill;
            previewLayer.Frame = new CGRect(0, 0, this.Frame.Width, this.Frame.Height);
            previewLayer.Position = new CGPoint(this.Layer.Bounds.Width / 2, (this.Layer.Bounds.Height / 2));

            layerView = new UIView(new CGRect(0, 0, this.Frame.Width, this.Frame.Height));
            layerView.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
            layerView.Layer.AddSublayer(previewLayer);

            AddSubview(layerView);

            ResizePreview(UIApplication.SharedApplication.StatusBarOrientation);

            if (overlayView != null)
            {
                AddSubview(overlayView);
                BringSubviewToFront(overlayView);
            }

            session.StartRunning();
            DeviceAutomaticVideoZoomFactor.ApplyAutomaticZoomFactorIfNeeded(captureDevice, 20);
            if (captureDevice.IsFocusModeSupported(AVCaptureFocusMode.ContinuousAutoFocus))
            {
                NSError err = null;
                if (captureDevice.LockForConfiguration(out err))
                {
                    if (captureDevice.IsFocusModeSupported(AVCaptureFocusMode.ContinuousAutoFocus))
                        captureDevice.FocusMode = AVCaptureFocusMode.ContinuousAutoFocus;
                    else if (captureDevice.IsFocusModeSupported(AVCaptureFocusMode.AutoFocus))
                        captureDevice.FocusMode = AVCaptureFocusMode.AutoFocus;

                    if (captureDevice.IsExposureModeSupported(AVCaptureExposureMode.ContinuousAutoExposure))
                        captureDevice.ExposureMode = AVCaptureExposureMode.ContinuousAutoExposure;
                    else if (captureDevice.IsExposureModeSupported(AVCaptureExposureMode.AutoExpose))
                        captureDevice.ExposureMode = AVCaptureExposureMode.AutoExpose;

                    if (captureDevice.IsWhiteBalanceModeSupported(AVCaptureWhiteBalanceMode.ContinuousAutoWhiteBalance))
                        captureDevice.WhiteBalanceMode = AVCaptureWhiteBalanceMode.ContinuousAutoWhiteBalance;
                    else if (captureDevice.IsWhiteBalanceModeSupported(AVCaptureWhiteBalanceMode.AutoWhiteBalance))
                        captureDevice.WhiteBalanceMode = AVCaptureWhiteBalanceMode.AutoWhiteBalance;

                    if (UIDevice.CurrentDevice.CheckSystemVersion(7, 0) && captureDevice.AutoFocusRangeRestrictionSupported)
                        captureDevice.AutoFocusRangeRestriction = AVCaptureAutoFocusRangeRestriction.None;

                    if (captureDevice.FocusPointOfInterestSupported)
                        captureDevice.FocusPointOfInterest = new CGPoint(ScanningOptions.FocusPointOfInterest.X, ScanningOptions.FocusPointOfInterest.Y);

                    if (captureDevice.ExposurePointOfInterestSupported)
                        captureDevice.ExposurePointOfInterest = new CGPoint(ScanningOptions.FocusPointOfInterest.X, ScanningOptions.FocusPointOfInterest.Y);

                    captureDevice.UnlockForConfiguration();
                }
                else
                    Console.WriteLine("Failed to Lock for Config: " + err.Description);
            }

            return true;
        }


        AVCaptureDevice GetBestAvailableBackFacingCamera()
        {
            var builtInTripleCamera = AVCaptureDevice.GetDefaultDevice(AVCaptureDeviceType.BuiltInTripleCamera, AVMediaTypes.Video, AVCaptureDevicePosition.Back);
            var builtInDualCamera = AVCaptureDevice.GetDefaultDevice(AVCaptureDeviceType.BuiltInDualCamera, AVMediaTypes.Video, AVCaptureDevicePosition.Back);
            var builtInWideAngleCamera = AVCaptureDevice.GetDefaultDevice(AVCaptureDeviceType.BuiltInWideAngleCamera, AVMediaTypes.Video, AVCaptureDevicePosition.Back);

            if (builtInDualCamera != null)
                return builtInDualCamera;
            else if (builtInWideAngleCamera != null)
                return builtInWideAngleCamera;
            else
                throw new Exception("Missing expected back camera device");
        }


        public void DidRotate(UIInterfaceOrientation orientation)
        {
            ResizePreview(orientation);

            LayoutSubviews();
        }

        public void ResizePreview(UIInterfaceOrientation orientation)
        {
            if (previewLayer == null)
                return;

            previewLayer.Frame = new CGRect(0, 0, Frame.Width, Frame.Height);

            if (previewLayer.RespondsToSelector(new Selector("connection")) && previewLayer.Connection != null)
            {
                switch (orientation)
                {
                    case UIInterfaceOrientation.LandscapeLeft:
                        previewLayer.Connection.VideoOrientation = AVCaptureVideoOrientation.LandscapeLeft;
                        break;
                    case UIInterfaceOrientation.LandscapeRight:
                        previewLayer.Connection.VideoOrientation = AVCaptureVideoOrientation.LandscapeRight;
                        break;
                    case UIInterfaceOrientation.Portrait:
                        previewLayer.Connection.VideoOrientation = AVCaptureVideoOrientation.Portrait;
                        break;
                    case UIInterfaceOrientation.PortraitUpsideDown:
                        previewLayer.Connection.VideoOrientation = AVCaptureVideoOrientation.PortraitUpsideDown;
                        break;
                }
            }
        }

        public void Focus(CGPoint pointOfInterest)
        {
            //Get the device
            if (AVMediaType.Video == null)
                return;

            var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);

            if (device == null)
                return;

            //See if it supports focusing on a point
            if (device.FocusPointOfInterestSupported && !device.AdjustingFocus)
            {
                NSError err = null;

                //Lock device to config
                if (device.LockForConfiguration(out err))
                {
                    Console.WriteLine("Focusing at point: " + pointOfInterest.X + ", " + pointOfInterest.Y);

                    //Focus at the point touched
                    device.FocusPointOfInterest = pointOfInterest;
                    device.FocusMode = AVCaptureFocusMode.ContinuousAutoFocus;
                    device.UnlockForConfiguration();
                }
            }
        }

        #region IZXingScanner implementation
        public void StartScanning(Action<Result> scanResultHandler, MobileBarcodeScanningOptions options = null)
        {
            if (!analyzing)
                analyzing = true;

            if (!stopped)
                return;

            Setup();

            ScanningOptions = options ?? MobileBarcodeScanningOptions.Default;
            resultCallback = scanResultHandler;

            InvokeOnMainThread(() =>
            {
                if (!SetupCaptureSession())
                {
                    //Setup 'simulated' view:
                    Console.WriteLine("Capture Session FAILED");
                }

                if (Runtime.Arch == Arch.SIMULATOR)
                {
                    var simView = new UIView(new CGRect(0, 0, this.Frame.Width, this.Frame.Height));
                    simView.BackgroundColor = UIColor.LightGray;
                    simView.AutoresizingMask = UIViewAutoresizing.FlexibleWidth | UIViewAutoresizing.FlexibleHeight;
                    InsertSubview(simView, 0);
                }
            });

            stopped = false;
        }


        public void StopScanning()
        {
            if (stopped)
                return;

            Console.WriteLine("Stopping...");

            //Try removing all existing outputs prior to closing the session
            try
            {
                while (session.Outputs.Length > 0)
                    session.RemoveOutput(session.Outputs[0]);
            }
            catch { }

            //Try to remove all existing inputs prior to closing the session
            try
            {
                while (session.Inputs.Length > 0)
                    session.RemoveInput(session.Inputs[0]);
            }
            catch { }

            if (session.Running)
                session.StopRunning();

            stopped = true;
        }

        public void PauseAnalysis()
            => analyzing = false;

        public void ResumeAnalysis()
            => analyzing = true;

        public void Torch(bool on)
        {
            try
            {
                var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
                if (device.HasFlash || device.HasTorch)
                {
                    device.LockForConfiguration(out var err);

                    if (on)
                    {
                        if (device.HasTorch)
                            device.TorchMode = AVCaptureTorchMode.On;
                        if (device.HasFlash)
                            device.FlashMode = AVCaptureFlashMode.On;
                    }
                    else
                    {
                        if (device.HasTorch)
                            device.TorchMode = AVCaptureTorchMode.Off;
                        if (device.HasFlash)
                            device.FlashMode = AVCaptureFlashMode.Off;
                    }

                    device.UnlockForConfiguration();
                }
                device = null;

                torch = on;
            }
            catch { }
        }

        public void ToggleTorch()
            => Torch(!IsTorchOn);

        public void AutoFocus()
        {
            //Doesn't do much on iOS :(
        }

        public void AutoFocus(int x, int y)
        {
            //Doesn't do much on iOS :(
        }

        public string TopText { get; set; }
        public string BottomText { get; set; }


        public UIView CustomOverlayView { get; set; }
        public bool UseCustomOverlayView { get; set; }
        public bool IsAnalyzing => analyzing;
        public bool IsTorchOn => torch;

        bool? hasTorch = null;

        public bool HasTorch
        {
            get
            {
                if (hasTorch.HasValue)
                    return hasTorch.Value;

                var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
                hasTorch = device.HasFlash || device.HasTorch;
                return hasTorch.Value;
            }
        }
        #endregion

        public static bool SupportsAllRequestedBarcodeFormats(IEnumerable<BarcodeFormat> formats)
        {
            var supported = new List<BarcodeFormat>() {
                BarcodeFormat.AZTEC, BarcodeFormat.CODE_128, BarcodeFormat.CODE_39,
                BarcodeFormat.CODE_93, BarcodeFormat.EAN_13, BarcodeFormat.EAN_8,
                BarcodeFormat.PDF_417, BarcodeFormat.QR_CODE, BarcodeFormat.UPC_E,
                BarcodeFormat.DATA_MATRIX, BarcodeFormat.ITF,
                BarcodeFormat.All_1D
            };

            return !formats.Any(f => !supported.Contains(f));
        }

        BarcodeFormat ZXingBarcodeFormatFromAVCaptureBarcodeFormat(string avMetadataObjectType)
        {
            switch (avMetadataObjectType)
            {
                case "AztecCode":
                    return BarcodeFormat.AZTEC;
                case "Code128Code":
                    return BarcodeFormat.CODE_128;
                case "Code39Code":
                    return BarcodeFormat.CODE_39;
                case "Code39Mod43Code":
                    return BarcodeFormat.CODE_39;
                case "Code93Code":
                    return BarcodeFormat.CODE_93;
                case "EAN13Code":
                    return BarcodeFormat.EAN_13;
                case "EAN8Code":
                    return BarcodeFormat.EAN_8;
                case "PDF417Code":
                    return BarcodeFormat.PDF_417;
                case "QRCode":
                    return BarcodeFormat.QR_CODE;
                case "UPCECode":
                    return BarcodeFormat.UPC_E;
                case "DataMatrixCode":
                    return BarcodeFormat.DATA_MATRIX;
                case "Interleaved2of5Code":
                    return BarcodeFormat.ITF;
                default:
                    return BarcodeFormat.QR_CODE;
            }
        }

        AVMetadataObjectType AVCaptureBarcodeFormatFromZXingBarcodeFormat(BarcodeFormat zxingBarcodeFormat)
        {
            var formats = AVMetadataObjectType.None;

            switch (zxingBarcodeFormat)
            {
                case BarcodeFormat.AZTEC:
                    formats |= AVMetadataObjectType.AztecCode;
                    break;
                case BarcodeFormat.CODE_128:
                    formats |= AVMetadataObjectType.Code128Code;
                    break;
                case BarcodeFormat.CODE_39:
                    formats |= AVMetadataObjectType.Code39Code;
                    formats |= AVMetadataObjectType.Code39Mod43Code;
                    break;
                case BarcodeFormat.CODE_93:
                    formats |= AVMetadataObjectType.Code93Code;
                    break;
                case BarcodeFormat.EAN_13:
                    formats |= AVMetadataObjectType.EAN13Code;
                    break;
                case BarcodeFormat.EAN_8:
                    formats |= AVMetadataObjectType.EAN8Code;
                    break;
                case BarcodeFormat.PDF_417:
                    formats |= AVMetadataObjectType.PDF417Code;
                    break;
                case BarcodeFormat.QR_CODE:
                    formats |= AVMetadataObjectType.QRCode;
                    break;
                case BarcodeFormat.UPC_E:
                    formats |= AVMetadataObjectType.UPCECode;
                    break;
                case BarcodeFormat.All_1D:
                    formats |= AVMetadataObjectType.UPCECode;
                    formats |= AVMetadataObjectType.EAN13Code;
                    formats |= AVMetadataObjectType.EAN8Code;
                    formats |= AVMetadataObjectType.Code39Code;
                    formats |= AVMetadataObjectType.Code39Mod43Code;
                    formats |= AVMetadataObjectType.Code93Code;
                    break;
                case BarcodeFormat.DATA_MATRIX:
                    formats |= AVMetadataObjectType.DataMatrixCode;
                    break;
                case BarcodeFormat.ITF:
                    formats |= AVMetadataObjectType.ITF14Code;
                    break;
                case BarcodeFormat.CODABAR:
                case BarcodeFormat.MAXICODE:
                case BarcodeFormat.MSI:
                case BarcodeFormat.PLESSEY:
                case BarcodeFormat.RSS_14:
                case BarcodeFormat.RSS_EXPANDED:
                case BarcodeFormat.UPC_A:
                    //TODO: Throw exception?
                    break;
            }

            return formats;
        }
    }

    class CaptureDelegate : AVCaptureMetadataOutputObjectsDelegate
    {
        public CaptureDelegate(Action<IEnumerable<AVMetadataObject>> onCapture)
            => OnCapture = onCapture;

        public Action<IEnumerable<AVMetadataObject>> OnCapture { get; set; }

        public override void DidOutputMetadataObjects(AVCaptureMetadataOutput captureOutput, AVMetadataObject[] metadataObjects, AVCaptureConnection connection)
        {
            if (OnCapture != null && metadataObjects != null)
                OnCapture(metadataObjects);
        }
    }

    /// <summary>
    /// DeviceAutomaticVideoZoomFactor
    /// </summary>
    public class DeviceAutomaticVideoZoomFactor
    {
        // /
        // / Optimize the user experience for scanning QR codes down to smaller sizes (determined by `minimumCodeSize`, for example 2x2 cm).
        // / When scanning a QR code of that size, the user may need to get closer than the camera's minimum focus distance to fill the rect of interest.
        // / To have the QR code both fill the rect and still be in focus, we may need to apply some zoom.
        // /
        /// <summary>
        /// ApplyAutomaticZoomFactorIfNeeded
        /// </summary>
        /// <param name="device"></param>
        /// <param name="minimumCodeSize"></param>
        public static void ApplyAutomaticZoomFactorIfNeeded(AVCaptureDevice device, nfloat minimumCodeSize)
        {
            if (UIDevice.CurrentDevice.CheckSystemVersion(15, 0))
            {
                var deviceMinimumFocusDistance = device.MinimumFocusDistance;

                if (!(deviceMinimumFocusDistance != -1))
                    return;

                if (device.ActiveFormat.FormatDescription is CMVideoFormatDescription formatDescription)
                {
                    var formatDimensions = formatDescription.Dimensions;
                    var deviceFieldOfView = device.ActiveFormat.VideoFieldOfView;
                    var rectOfInterestWidth = Convert.ToDecimal(formatDimensions.Height) / Convert.ToDecimal(formatDimensions.Width); //Double(formatDimensions.height) / Double(formatDimensions.width);
                    var minimumSubjectDistanceForCode = GetMinimumSubjectDistanceForCode(deviceFieldOfView, minimumCodeSize, new nfloat((float)rectOfInterestWidth));
                    Console.WriteLine($"minimumSubjectDistanceForCode: {minimumSubjectDistanceForCode}");
                    Console.WriteLine($"Video Zoom Factor minimum subject distance: {minimumSubjectDistanceForCode}");

                    /*if (minimumSubjectDistanceForCode < deviceMinimumFocusDistance)
                        return;*/

                    var zoomFactor = deviceMinimumFocusDistance / minimumSubjectDistanceForCode;
                    Console.WriteLine($"Video Zoom Factor computed zoom factor: {zoomFactor}");
                    device.LockForConfiguration(out var error); //LockForConfiguration();
                    device.VideoZoomFactor = zoomFactor;
                    device.UnlockForConfiguration();
                    Console.WriteLine($"Video Zoom Factor applied zoom factor: {device.VideoZoomFactor}");
                }
            }
        }

        /// <summary>
        /// GetMinimumSubjectDistanceForCode
        /// </summary>
        /// <param name="fieldOfView"></param>
        /// <param name="minimumCodeSize"></param>
        /// <param name="previewFillPercentage"></param>
        /// <returns></returns>
        static nfloat GetMinimumSubjectDistanceForCode(nfloat fieldOfView, nfloat minimumCodeSize, nfloat previewFillPercentage)
        {
            // 
            //             Given the camera horizontal field of view, we can compute the distance (mm) to make a code
            //             of minimumCodeSize (mm) fill the previewFillPercentage.
            //          
            var radians = ToRadians(fieldOfView / 2);
            var filledCodeSize = minimumCodeSize / previewFillPercentage;
            return filledCodeSize / new nfloat(Math.Tan(radians));
        }

        /// <summary>
        /// To Radians
        /// </summary>
        /// <param name="degrees"></param>
        /// <returns></returns>
        static nfloat ToRadians(nfloat degrees)
        {
            return degrees * new nfloat(Math.PI) / 180;
        }
    }
}

