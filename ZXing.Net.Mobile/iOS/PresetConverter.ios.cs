using AVFoundation;
using Foundation;

namespace ZXing.Mobile
{
    public static class PresetConverter
    {
        public static NSString ToAVCaptureSessionPreset(CameraResolutionPreset preset)
        {
            switch (preset)
            {
                case CameraResolutionPreset.PresetLow:
                    return AVCaptureSession.PresetLow;
                case CameraResolutionPreset.PresetPhoto:
                    return AVCaptureSession.PresetPhoto;
                case CameraResolutionPreset.Preset352x288:
                    return AVCaptureSession.Preset352x288;
                case CameraResolutionPreset.PresetMedium:
                    return AVCaptureSession.PresetMedium;
                case CameraResolutionPreset.PresetHigh:
                    return AVCaptureSession.PresetHigh;
                case CameraResolutionPreset.Preset640x480:
                    return AVCaptureSession.Preset640x480;
                case CameraResolutionPreset.Preset1280x720:
                    return AVCaptureSession.Preset1280x720;
                case CameraResolutionPreset.Preset1920x1080:
                    return AVCaptureSession.Preset1920x1080;
                case CameraResolutionPreset.Preset3840x2160:
                    return AVCaptureSession.Preset3840x2160;
                default:
                    return AVCaptureSession.Preset640x480;
            }
        }
    }
}
