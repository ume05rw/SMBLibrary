/* Copyright (C) 2017 Tal Aloni <tal.aloni.il@gmail.com>. All rights reserved.
 * 
 * You can redistribute this program and/or modify it under the terms of
 * the GNU Lesser Public License as published by the Free Software Foundation,
 * either version 3 of the License, or (at your option) any later version.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Utilities;

namespace SMBLibrary.Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct UNICODE_STRING : IDisposable
    {
        public ushort Length;
        public ushort MaximumLength;
        private IntPtr Buffer;

        public UNICODE_STRING(string value)
        {
            Length = (ushort)(value.Length * 2);
            MaximumLength = (ushort)(value.Length + 2);
            Buffer = Marshal.StringToHGlobalUni(value);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Buffer);
            Buffer = IntPtr.Zero;
        }

        public override string ToString()
        {
            return Marshal.PtrToStringUni(Buffer);
        }
    }

    [StructLayoutAttribute(LayoutKind.Sequential)]
    public struct OBJECT_ATTRIBUTES
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayoutAttribute(LayoutKind.Sequential)]
    public struct IO_STATUS_BLOCK
    {
        public UInt32 Status;
        public IntPtr Information;
    }

    internal class PendingRequest
    {
        public IntPtr FileHandle;
        public uint ThreadID;
        public IO_STATUS_BLOCK IOStatusBlock;
        public bool Cleanup;
    }

    public class NTDirectoryFileSystem : INTFileStore
    {
        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtCreateFile(out IntPtr handle, uint desiredAccess, ref OBJECT_ATTRIBUTES objectAttributes, out IO_STATUS_BLOCK ioStatusBlock, ref long allocationSize, FileAttributes fileAttributes, ShareAccess shareAccess, CreateDisposition createDisposition, CreateOptions createOptions, IntPtr eaBuffer, uint eaLength);

        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtClose(IntPtr handle);
        
        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtReadFile(IntPtr handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, out IO_STATUS_BLOCK ioStatusBlock, byte[] buffer, uint length, ref long byteOffset, IntPtr key);

        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtWriteFile(IntPtr handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, out IO_STATUS_BLOCK ioStatusBlock, byte[] buffer, uint length, ref long byteOffset, IntPtr key);

        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtFlushBuffersFile(IntPtr handle, out IO_STATUS_BLOCK ioStatusBlock);
        
        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtQueryDirectoryFile(IntPtr handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, out IO_STATUS_BLOCK ioStatusBlock, byte[] fileInformation, uint length, uint fileInformationClass, bool returnSingleEntry, ref UNICODE_STRING fileName, bool restartScan);

        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtQueryInformationFile(IntPtr handle, out IO_STATUS_BLOCK ioStatusBlock, byte[] fileInformation, uint length, uint fileInformationClass);

        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtSetInformationFile(IntPtr handle, out IO_STATUS_BLOCK ioStatusBlock, byte[] fileInformation, uint length, uint fileInformationClass);

        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtQueryVolumeInformationFile(IntPtr handle, out IO_STATUS_BLOCK ioStatusBlock, byte[] fsInformation, uint length, uint fsInformationClass);

        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtNotifyChangeDirectoryFile(IntPtr handle, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, out IO_STATUS_BLOCK ioStatusBlock, byte[] buffer, uint bufferSize, NotifyChangeFilter completionFilter, bool watchTree);

        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtAlertThread(IntPtr threadHandle);

        // Available starting from Windows Vista.
        [DllImport("ntdll.dll", ExactSpelling = true, SetLastError = false)]
        private static extern NTStatus NtCancelSynchronousIoFile(IntPtr threadHandle, ref IO_STATUS_BLOCK ioRequestToCancel, out IO_STATUS_BLOCK ioStatusBlock);

        private DirectoryInfo m_directory;
        private PendingRequestCollection m_pendingRequests = new PendingRequestCollection();

        public NTDirectoryFileSystem(string path) : this(new DirectoryInfo(path))
        {

        }

        public NTDirectoryFileSystem(DirectoryInfo directory)
        {
            m_directory = directory;
        }

        private OBJECT_ATTRIBUTES InitializeObjectAttributes(UNICODE_STRING objectName)
        {
            OBJECT_ATTRIBUTES objectAttributes = new OBJECT_ATTRIBUTES();
            objectAttributes.RootDirectory = IntPtr.Zero;
            objectAttributes.ObjectName = Marshal.AllocHGlobal(Marshal.SizeOf(objectName));
            Marshal.StructureToPtr(objectName, objectAttributes.ObjectName, false);
            objectAttributes.SecurityDescriptor = IntPtr.Zero;
            objectAttributes.SecurityQualityOfService = IntPtr.Zero;

            objectAttributes.Length = Marshal.SizeOf(objectAttributes);
            return objectAttributes;
        }

        private NTStatus CreateFile(out IntPtr handle, out FileStatus fileStatus, string nativePath, AccessMask desiredAccess, long allocationSize, FileAttributes fileAttributes, ShareAccess shareAccess, CreateDisposition createDisposition, CreateOptions createOptions)
        {
            UNICODE_STRING objectName = new UNICODE_STRING(nativePath);
            OBJECT_ATTRIBUTES objectAttributes = InitializeObjectAttributes(objectName);
            IO_STATUS_BLOCK ioStatusBlock;
            NTStatus status = NtCreateFile(out handle, (uint)desiredAccess, ref objectAttributes, out ioStatusBlock, ref allocationSize, fileAttributes, shareAccess, createDisposition, createOptions, IntPtr.Zero, 0);
            fileStatus = (FileStatus)ioStatusBlock.Information;
            return status;
        }

        private string ToNativePath(string path)
        {
            if (!path.StartsWith(@"\"))
            {
                path = @"\" + path;
            }
            return @"\??\" + m_directory.FullName + path;
        }

        public NTStatus CreateFile(out object handle, out FileStatus fileStatus, string path, AccessMask desiredAccess, FileAttributes fileAttributes, ShareAccess shareAccess, CreateDisposition createDisposition, CreateOptions createOptions, SecurityContext securityContext)
        {
            IntPtr fileHandle;
            string nativePath = ToNativePath(path);
            // NtQueryDirectoryFile will return STATUS_PENDING if the directory handle was not opened with SYNCHRONIZE and FILE_SYNCHRONOUS_IO_ALERT or FILE_SYNCHRONOUS_IO_NONALERT.
            // Our usage of NtNotifyChangeDirectoryFile assumes the directory handle is opened with SYNCHRONIZE and FILE_SYNCHRONOUS_IO_ALERT (or FILE_SYNCHRONOUS_IO_NONALERT starting from Windows Vista).
            // Note: Sometimes a directory will be opened without specifying FILE_DIRECTORY_FILE.
            desiredAccess.Directory |= DirectoryAccessMask.SYNCHRONIZE;
            createOptions &= ~CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT;
            createOptions |= CreateOptions.FILE_SYNCHRONOUS_IO_ALERT;

            if ((createOptions & CreateOptions.FILE_NO_INTERMEDIATE_BUFFERING) > 0 &&
                (desiredAccess.File & FileAccessMask.FILE_APPEND_DATA) > 0)
            {
                // FILE_NO_INTERMEDIATE_BUFFERING is incompatible with FILE_APPEND_DATA
                // [MS-SMB2] 3.3.5.9 suggests setting FILE_APPEND_DATA to zero in this case.
                desiredAccess = (AccessMask)((uint)desiredAccess & (uint)~FileAccessMask.FILE_APPEND_DATA);
            }

            NTStatus status = CreateFile(out fileHandle, out fileStatus, nativePath, desiredAccess, 0, fileAttributes, shareAccess, createDisposition, createOptions);
            handle = fileHandle;
            return status;
        }

        public NTStatus CloseFile(object handle)
        {
            // [MS-FSA] 2.1.5.4 The close operation has to complete any pending ChangeNotify request with STATUS_NOTIFY_CLEANUP.
            // - When closing a synchronous handle we must explicitly cancel any pending ChangeNotify request, otherwise the call to NtClose will hang.
            //   We use request.Cleanup to tell that we should complete such ChangeNotify request with STATUS_NOTIFY_CLEANUP.
            // - When closing an asynchronous handle Windows will implicitly complete any pending ChangeNotify request with STATUS_NOTIFY_CLEANUP as required.
            List<PendingRequest> pendingRequests = m_pendingRequests.GetRequestsByHandle((IntPtr)handle);
            foreach (PendingRequest request in pendingRequests)
            {
                request.Cleanup = true;
                Cancel(request);
            }
            return NtClose((IntPtr)handle);
        }

        public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount)
        {
            IO_STATUS_BLOCK ioStatusBlock;
            data = new byte[maxCount];
            NTStatus status = NtReadFile((IntPtr)handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out ioStatusBlock, data, (uint)maxCount, ref offset, IntPtr.Zero);
            if (status == NTStatus.STATUS_SUCCESS)
            {
                int bytesRead = (int)ioStatusBlock.Information;
                if (bytesRead < maxCount)
                {
                    data = ByteReader.ReadBytes(data, 0, bytesRead);
                }
            }
            return status;
        }

        public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
        {
            IO_STATUS_BLOCK ioStatusBlock;
            NTStatus status = NtWriteFile((IntPtr)handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out ioStatusBlock, data, (uint)data.Length, ref offset, IntPtr.Zero);
            if (status == NTStatus.STATUS_SUCCESS)
            {
                numberOfBytesWritten = (int)ioStatusBlock.Information;
            }
            else
            {
                numberOfBytesWritten = 0;
            }
            return status;
        }

        public NTStatus FlushFileBuffers(object handle)
        {
            IO_STATUS_BLOCK ioStatusBlock;
            return NtFlushBuffersFile((IntPtr)handle, out ioStatusBlock);
        }

        public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result, object handle, string fileName, FileInformationClass informationClass)
        {
            IO_STATUS_BLOCK ioStatusBlock;
            byte[] buffer = new byte[4096];
            UNICODE_STRING fileNameStructure = new UNICODE_STRING(fileName);
            result = new List<QueryDirectoryFileInformation>();
            bool restartScan = true;
            while (true)
            {
                NTStatus status = NtQueryDirectoryFile((IntPtr)handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out ioStatusBlock, buffer, (uint)buffer.Length, (byte)informationClass, false, ref fileNameStructure, restartScan);
                if (status == NTStatus.STATUS_NO_MORE_FILES)
                {
                    break;
                }
                else if (status != NTStatus.STATUS_SUCCESS)
                {
                    return status;
                }
                restartScan = false;
                List<QueryDirectoryFileInformation> page = QueryDirectoryFileInformation.ReadFileInformationList(buffer, 0, informationClass);
                result.AddRange(page);
            }
            fileNameStructure.Dispose();
            return NTStatus.STATUS_SUCCESS;
        }

        public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass)
        {
            IO_STATUS_BLOCK ioStatusBlock;
            byte[] buffer = new byte[8192];
            NTStatus status = NtQueryInformationFile((IntPtr)handle, out ioStatusBlock, buffer, (uint)buffer.Length, (uint)informationClass);
            if (status == NTStatus.STATUS_SUCCESS)
            {
                result = FileInformation.GetFileInformation(buffer, 0, informationClass);
            }
            else
            {
                result = null;
            }
            return status;
        }

        public NTStatus SetFileInformation(object handle, FileInformation information)
        {
            IO_STATUS_BLOCK ioStatusBlock;
            if (information is FileRenameInformationType2)
            {
                FileRenameInformationType2 fileRenameInformation2 = (FileRenameInformationType2)information;
                fileRenameInformation2.FileName = ToNativePath(fileRenameInformation2.FileName);

                // Note: WOW64 process should use FILE_RENAME_INFORMATION_TYPE_1.
                // Note: Server 2003 x64 has issues with using FILE_RENAME_INFORMATION under WOW64.
                if (!ProcessHelper.Is64BitProcess)
                {
                    FileRenameInformationType1 fileRenameInformation1 = new FileRenameInformationType1();
                    fileRenameInformation1.ReplaceIfExists = fileRenameInformation2.ReplaceIfExists;
                    fileRenameInformation1.FileName = fileRenameInformation2.FileName;
                    information = fileRenameInformation1;
                }
            }
            else if (information is FileLinkInformationType2)
            {
                FileLinkInformationType2 fileLinkInformation2 = (FileLinkInformationType2)information;
                fileLinkInformation2.FileName = ToNativePath(fileLinkInformation2.FileName);

                if (!ProcessHelper.Is64BitProcess)
                {
                    FileLinkInformationType1 fileLinkInformation1 = new FileLinkInformationType1();
                    fileLinkInformation1.ReplaceIfExists = fileLinkInformation2.ReplaceIfExists;
                    fileLinkInformation1.FileName = fileLinkInformation2.FileName;
                    information = fileLinkInformation1;
                }
            }
            byte[] buffer = information.GetBytes();
            return NtSetInformationFile((IntPtr)handle, out ioStatusBlock, buffer, (uint)buffer.Length, (uint)information.FileInformationClass);
        }

        public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass)
        {
            IO_STATUS_BLOCK ioStatusBlock;
            byte[] buffer = new byte[4096];
            IntPtr volumeHandle;
            FileStatus fileStatus;
            string nativePath = @"\??\" + m_directory.FullName.Substring(0, 3);
            NTStatus status = CreateFile(out volumeHandle, out fileStatus, nativePath, DirectoryAccessMask.GENERIC_READ, 0, (FileAttributes)0, ShareAccess.FILE_SHARE_READ, CreateDisposition.FILE_OPEN, (CreateOptions)0);
            result = null;
            if (status != NTStatus.STATUS_SUCCESS)
            {
                return status;
            }
            status = NtQueryVolumeInformationFile((IntPtr)volumeHandle, out ioStatusBlock, buffer, (uint)buffer.Length, (uint)informationClass);
            CloseFile(volumeHandle);
            if (status == NTStatus.STATUS_SUCCESS)
            {
                result = FileSystemInformation.GetFileSystemInformation(buffer, 0, informationClass);
            }
            return status;
        }

        public NTStatus NotifyChange(out object ioRequest, object handle, NotifyChangeFilter completionFilter, bool watchTree, int outputBufferSize, OnNotifyChangeCompleted onNotifyChangeCompleted, object context)
        {
            byte[] buffer = new byte[outputBufferSize];
            ManualResetEvent requestAddedEvent = new ManualResetEvent(false);
            PendingRequest request = new PendingRequest();
            Thread m_thread = new Thread(delegate()
            {
                request.FileHandle = (IntPtr)handle;
                request.ThreadID = ThreadingHelper.GetCurrentThreadId();
                m_pendingRequests.Add(request);
                // The request has been added, we can now return STATUS_PENDING.
                requestAddedEvent.Set();
                NTStatus status = NtNotifyChangeDirectoryFile((IntPtr)handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out request.IOStatusBlock, buffer, (uint)buffer.Length, completionFilter, watchTree);
                if (status == NTStatus.STATUS_SUCCESS)
                {
                    int length = (int)request.IOStatusBlock.Information;
                    buffer = ByteReader.ReadBytes(buffer, 0, length);
                }
                else
                {
                    const NTStatus STATUS_ALERTED = (NTStatus)0x00000101;
                    const NTStatus STATUS_OBJECT_TYPE_MISMATCH = (NTStatus)0xC0000024;

                    buffer = new byte[0];
                    if (status == STATUS_OBJECT_TYPE_MISMATCH)
                    {
                        status = NTStatus.STATUS_INVALID_HANDLE;
                    }
                    else if (status == STATUS_ALERTED)
                    {
                        status = NTStatus.STATUS_CANCELLED;
                    }

                    // If the handle is closing and we had to cancel a ChangeNotify request as part of a cleanup,
                    // we return STATUS_NOTIFY_CLEANUP as specified in [MS-FSA] 2.1.5.4.
                    if (status == NTStatus.STATUS_CANCELLED && request.Cleanup)
                    {
                        status = NTStatus.STATUS_NOTIFY_CLEANUP;
                    }
                }
                onNotifyChangeCompleted(status, buffer, context);
                m_pendingRequests.Remove((IntPtr)handle, request.ThreadID);
            });
            m_thread.Start();

            // We must wait for the request to be added in order for Cancel to function properly.
            requestAddedEvent.WaitOne();
            ioRequest = request;
            return NTStatus.STATUS_PENDING;
        }

        public NTStatus Cancel(object ioRequest)
        {
            PendingRequest request = (PendingRequest)ioRequest;
            const uint THREAD_TERMINATE = 0x00000001;
            const uint THREAD_ALERT = 0x00000004;
            uint threadID = request.ThreadID;
            IntPtr threadHandle = ThreadingHelper.OpenThread(THREAD_TERMINATE | THREAD_ALERT, false, threadID);
            if (threadHandle == IntPtr.Zero)
            {
                Win32Error error = (Win32Error)Marshal.GetLastWin32Error();
                if (error == Win32Error.ERROR_INVALID_PARAMETER)
                {
                    return NTStatus.STATUS_INVALID_HANDLE;
                }
                else
                {
                    throw new Exception("OpenThread failed, Win32 error: " + error.ToString("D"));
                }
            }

            NTStatus status;
            if (Environment.OSVersion.Version.Major >= 6)
            {
                IO_STATUS_BLOCK ioStatusBlock;
                status = NtCancelSynchronousIoFile(threadHandle, ref request.IOStatusBlock, out ioStatusBlock);
            }
            else
            {
                // The handle was opened for synchronous operation so NtNotifyChangeDirectoryFile is blocking.
                // We MUST use NtAlertThread to send a signal to stop the wait. The handle cannot be closed otherwise.
                // Note: The handle was opened with CreateOptions.FILE_SYNCHRONOUS_IO_ALERT as required.
                status = NtAlertThread(threadHandle);
            }

            ThreadingHelper.CloseHandle(threadHandle);
            m_pendingRequests.Remove(request.FileHandle, request.ThreadID);
            return status;
        }

        public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength)
        {
            output = null;
            return NTStatus.STATUS_NOT_SUPPORTED;
        }
    }
}
