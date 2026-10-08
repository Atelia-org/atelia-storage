namespace Atelia.Binary;

// Wire methods are independent of ValueCompression, which describes a writer request.
// Add a method only when both its encoder and strict decoder have been qualified.
internal enum ControlledValueStorage : byte {
    Null = 0,
    Raw = 1,
    Brotli = 2,
    Lz4Block = 3
}
