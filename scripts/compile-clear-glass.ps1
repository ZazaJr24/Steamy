$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public static class ClearGlassCompiler {
 [ComImport,Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
 interface IBlob { [PreserveSig] IntPtr GetBufferPointer(); [PreserveSig] UIntPtr GetBufferSize(); }
 [DllImport("d3dcompiler_47.dll",CharSet=CharSet.Ansi)]
 static extern int D3DCompile(byte[] data, UIntPtr length, string name, IntPtr macros, IntPtr include, string entry, string target, uint flags, uint flags2, out IntPtr code, out IntPtr errors);
 public static void Compile(string source, string output) {
  var bytes=File.ReadAllBytes(source); IntPtr code,errors;
  var result=D3DCompile(bytes,(UIntPtr)bytes.Length,source,IntPtr.Zero,IntPtr.Zero,"main","ps_3_0",0x8000,0,out code,out errors);
  try {
   if(result<0) { var error=(IBlob)Marshal.GetObjectForIUnknown(errors); throw new Exception(Marshal.PtrToStringAnsi(error.GetBufferPointer())); }
   var blob=(IBlob)Marshal.GetObjectForIUnknown(code); var compiled=new byte[(int)blob.GetBufferSize().ToUInt64()];
   Marshal.Copy(blob.GetBufferPointer(),compiled,0,compiled.Length); File.WriteAllBytes(output,compiled);
  } finally { if(code!=IntPtr.Zero)Marshal.Release(code);if(errors!=IntPtr.Zero)Marshal.Release(errors); }
 }
}
'@
$taskShaderRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'src/Steamy/Resources/LiquidGlass'
[ClearGlassCompiler]::Compile((Join-Path $taskShaderRoot 'ClearGlass.hlsl'), (Join-Path $taskShaderRoot 'ClearGlass.ps'))
