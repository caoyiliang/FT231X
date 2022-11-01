using Android.Content;
using Android.Hardware.Usb;
using Communication.Exceptions;
using Communication.Interfaces;
using Java.Lang;
using System.IO.Ports;

namespace FT231X
{
    public class SerialPort : IPhysicalPort
    {
        private static int REQTYPE_HOST_TO_DEVICE = UsbConstants.UsbTypeVendor | 128; // UsbConstants.USB_DIR_OUT;
        private static int SET_BAUD_RATE_REQUEST = 3;
        private static int SET_DATA_REQUEST = 4;
        private static int RESET_REQUEST = 0;
        private static int RESET_ALL = 0;

        private static int USB_WRITE_TIMEOUT_MILLIS = 0;
        private readonly Context _context;
        private readonly UsbManager _usbManager;
        private readonly UsbDevice _usbDevice;
        private readonly int _baudRate;
        private readonly int _dataBits;
        private readonly StopBits _stopBits;
        private readonly Parity _parity;
        private readonly UsbInterface _usbInterface;
        private UsbDeviceConnection? _usbDeviceConnection;
        private UsbEndpoint? _usbEndpointIn;
        private UsbEndpoint? _usbEndpointOut;
        private bool _isOpen;

        public SerialPort(Context context) : this(context, 9600, 8, StopBits.One, Parity.None) { }
        public SerialPort(Context context, int baudRate) : this(context, baudRate, 8, StopBits.One, Parity.None) { }
        public SerialPort(Context context, int baudRate, int dataBits) : this(context, baudRate, dataBits, StopBits.One, Parity.None) { }
        public SerialPort(Context context, int baudRate, int dataBits, StopBits stopBits) : this(context, baudRate, dataBits, stopBits, Parity.None) { }
        public SerialPort(Context context, int baudRate, int dataBits, StopBits stopBits, Parity parity)
        {
            _context = context;
            _usbManager = (UsbManager)context.GetSystemService(Context.UsbService)!;
            _usbDevice = _usbManager.DeviceList!.ToList()[0].Value;
            _usbInterface = _usbDevice.GetInterface(0);
            _baudRate = baudRate;
            _dataBits = dataBits;
            _stopBits = stopBits;
            _parity = parity;
        }

        private void SetParameters(int baudRate, int dataBits, StopBits stopBits, Parity parity)
        {
            if (_usbDeviceConnection is null) throw new NotConnectedException();

            if (baudRate <= 0)
            {
                throw new IllegalArgumentException("Invalid baud rate: " + baudRate);
            }

            SetBaudRate(baudRate);

            var _config = dataBits;

            switch (dataBits)
            {
                case 5:
                case 6:
                    throw new UnsupportedOperationException("Unsupported data bits: " + dataBits);
                case 7:
                case 8:
                    _config |= dataBits;
                    break;
                default:
                    throw new IllegalArgumentException("Invalid data bits: " + dataBits);
            }

            switch (parity)
            {
                case Parity.None:
                    break;
                case Parity.Odd:
                    _config |= 0x100;
                    break;
                case Parity.Even:
                    _config |= 0x200;
                    break;
                case Parity.Mark:
                    _config |= 0x300;
                    break;
                case Parity.Space:
                    _config |= 0x400;
                    break;
                default:
                    throw new IllegalArgumentException("Unknown parity value: " + parity);
            }

            switch (stopBits)
            {
                case StopBits.One:
                    break;
                case StopBits.OnePointFive:
                    throw new UnsupportedOperationException("Unsupported stop bits: 1.5");
                case StopBits.Two:
                    _config |= 0x1000;
                    break;
                default:
                    throw new IllegalArgumentException("Unknown stopBits value: " + stopBits);
            }

            int result = _usbDeviceConnection.ControlTransfer((UsbAddressing)REQTYPE_HOST_TO_DEVICE, SET_DATA_REQUEST, _config, 1, null, 0, USB_WRITE_TIMEOUT_MILLIS);

            if (result != 0)
            {
                throw new IOException("Setting parameters failed: result=" + result);
            }
        }

        private int SetBaudRate(int baudRate)
        {
            if (_usbDeviceConnection is null) throw new NotConnectedException();

            int divisor, subdivisor, effectiveBaudRate;

            if (baudRate > 3500000)
            {
                throw new UnsupportedOperationException("Baud rate to high");
            }
            else if (baudRate >= 2500000)
            {
                divisor = 0;
                subdivisor = 0;
                effectiveBaudRate = 3000000;
            }
            else if (baudRate >= 1750000)
            {
                divisor = 1;
                subdivisor = 0;
                effectiveBaudRate = 2000000;
            }
            else
            {
                divisor = (24000000 << 1) / baudRate;
                divisor = (divisor + 1) >> 1; // round
                subdivisor = divisor & 0x07;
                divisor >>= 3;
                if (divisor > 0x3fff) // exceeds bit 13 at 183 baud
                    throw new UnsupportedOperationException("Baud rate to low");
                effectiveBaudRate = (24000000 << 1) / ((divisor << 3) + subdivisor);
                effectiveBaudRate = (effectiveBaudRate + 1) >> 1;
            }
            double baudRateError = System.Math.Abs(1.0 - (effectiveBaudRate / (double)baudRate));
            if (baudRateError >= 0.031) // can happen only > 1.5Mbaud
                throw new UnsupportedOperationException(string.Format("Baud rate deviation %.1f%% is higher than allowed 3%%", baudRateError * 100));
            int value = divisor;
            int index = 0;
            switch (subdivisor)
            {
                case 0: break; // 16,15,14 = 000 - sub-integer divisor = 0
                case 4: value |= 0x4000; break; // 16,15,14 = 001 - sub-integer divisor = 0.5
                case 2: value |= 0x8000; break; // 16,15,14 = 010 - sub-integer divisor = 0.25
                case 1: value |= 0xc000; break; // 16,15,14 = 011 - sub-integer divisor = 0.125
                case 3: value |= 0x0000; index |= 1; break; // 16,15,14 = 100 - sub-integer divisor = 0.375
                case 5: value |= 0x4000; index |= 1; break; // 16,15,14 = 101 - sub-integer divisor = 0.625
                case 6: value |= 0x8000; index |= 1; break; // 16,15,14 = 110 - sub-integer divisor = 0.75
                case 7: value |= 0xc000; index |= 1; break; // 16,15,14 = 111 - sub-integer divisor = 0.875
            }
            int result = _usbDeviceConnection.ControlTransfer((UsbAddressing)REQTYPE_HOST_TO_DEVICE, SET_BAUD_RATE_REQUEST, value, index, null, 0, USB_WRITE_TIMEOUT_MILLIS);

            if (result != 0)
            {
                throw new IOException("Setting baudrate failed: result=" + result);
            }

            return effectiveBaudRate;
        }

        public bool IsOpen => _isOpen;

        public async Task CloseAsync()
        {
            _usbDeviceConnection?.Close();
            _isOpen = false;
            await Task.CompletedTask;
        }

        public void Dispose()
        {
        }

        public async Task OpenAsync()
        {
            if (await _usbManager!.RequestPermissionAsync(_usbDevice!, _context))
            {
                _usbDeviceConnection = _usbManager?.OpenDevice(_usbDevice);
                if (_usbDeviceConnection is null) throw new ConnectFailedException();
                _usbDeviceConnection.ClaimInterface(_usbInterface, true);

                int result = _usbDeviceConnection.ControlTransfer((UsbAddressing)REQTYPE_HOST_TO_DEVICE, RESET_REQUEST, RESET_ALL, 1, null, 0, USB_WRITE_TIMEOUT_MILLIS);
                if (result != 0)
                {
                    throw new IOException("Reset failed: result=" + result);
                }
                SetParameters(_baudRate, _dataBits, _stopBits, _parity);
                for (int index = 0; index < _usbInterface.EndpointCount; index++)
                {
                    var point = _usbInterface.GetEndpoint(index);
                    if (point!.Type == UsbAddressing.XferBulk)
                    {
                        if (point.Direction == UsbAddressing.In)
                        {
                            _usbEndpointIn = point;
                        }
                        else if (point.Direction == UsbAddressing.Out)
                        {
                            _usbEndpointOut = point;
                        }
                    }
                }
                _isOpen = true;
            }
        }

        public async Task<ReadDataResult> ReadDataAsync(int count, CancellationToken cancellationToken)
        {
            var mReadBuffer = new byte[count];

            int i = 0;
            while (i <= 2)
            {
                i = await _usbDeviceConnection!.BulkTransferAsync(_usbEndpointIn, mReadBuffer, count, USB_WRITE_TIMEOUT_MILLIS);
            }
            return new ReadDataResult
            {
                Length = i - 2,
                Data = mReadBuffer.ToList().Skip(2).ToArray()
            };
        }

        public async Task SendDataAsync(byte[] data, CancellationToken cancellationToken)
        {
            await _usbDeviceConnection!.BulkTransferAsync(_usbEndpointOut, data, data.Length, USB_WRITE_TIMEOUT_MILLIS);
        }
    }
}
