//go:build windows

package main

import (
	"bytes"
	"errors"
	"image"
	"image/png"
	"unsafe"

	"golang.org/x/sys/windows"
)

// 取某个可执行文件的图标，编码成 PNG。
//
// 走 shell 的 SHGetFileInfo 拿 HICON（这就是资源管理器显示的那个图标），
// 再画进一块 32 位 DIB 读出像素。不用 .NET、不落临时文件，服务端保持单文件零依赖。
var (
	modShell32 = windows.NewLazySystemDLL("shell32.dll")
	modGdi32   = windows.NewLazySystemDLL("gdi32.dll")

	procSHGetFileInfoW     = modShell32.NewProc("SHGetFileInfoW")
	procDestroyIcon        = modUser32.NewProc("DestroyIcon")
	procGetDC              = modUser32.NewProc("GetDC")
	procReleaseDC          = modUser32.NewProc("ReleaseDC")
	procGetIconInfo        = modUser32.NewProc("GetIconInfo")
	procDrawIconEx         = modUser32.NewProc("DrawIconEx")
	procGetObjectW         = modGdi32.NewProc("GetObjectW")
	procCreateDIBSection   = modGdi32.NewProc("CreateDIBSection")
	procCreateCompatibleDC = modGdi32.NewProc("CreateCompatibleDC")
	procSelectObject       = modGdi32.NewProc("SelectObject")
	procDeleteDC           = modGdi32.NewProc("DeleteDC")
	procDeleteObject       = modGdi32.NewProc("DeleteObject")
)

const (
	shgfiIcon      = 0x000000100
	shgfiLargeIcon = 0x000000000
	diNormal       = 3
	dibRGBColors   = 0
)

type shFileInfo struct {
	Icon        uintptr // HICON
	IconIndex   int32
	Attributes  uint32
	DisplayName [260]uint16
	TypeName    [80]uint16
}

type iconInfo struct {
	FIcon    int32
	XHotspot uint32
	YHotspot uint32
	HbmMask  uintptr
	HbmColor uintptr
}

type winBitmap struct {
	BmType       int32
	BmWidth      int32
	BmHeight     int32
	BmWidthBytes int32
	BmPlanes     uint16
	BmBitsPixel  uint16
	BmBits       uintptr
}

type bitmapInfoHeader struct {
	Size          uint32
	Width         int32
	Height        int32
	Planes        uint16
	BitCount      uint16
	Compression   uint32
	SizeImage     uint32
	XPelsPerMeter int32
	YPelsPerMeter int32
	ClrUsed       uint32
	ClrImportant  uint32
}

type bitmapInfo struct {
	Header bitmapInfoHeader
	Colors [1]uint32
}

// appIconPNG 返回该可执行文件图标的 PNG 字节。图标本身就取不到时返回错误，
// 上层据此留空即可，不必当成故障。
func appIconPNG(exe string) ([]byte, error) {
	path, err := windows.UTF16PtrFromString(exe)
	if err != nil {
		return nil, err
	}

	var shfi shFileInfo
	r, _, _ := procSHGetFileInfoW.Call(
		uintptr(unsafe.Pointer(path)), 0,
		uintptr(unsafe.Pointer(&shfi)), unsafe.Sizeof(shfi),
		shgfiIcon|shgfiLargeIcon)
	if r == 0 || shfi.Icon == 0 {
		return nil, errors.New("取不到图标")
	}
	hicon := shfi.Icon
	defer procDestroyIcon.Call(hicon)

	// 从黑白掩码位图读尺寸：彩色位图在部分图标里是空的
	var ii iconInfo
	if r0, _, _ := procGetIconInfo.Call(hicon, uintptr(unsafe.Pointer(&ii))); r0 == 0 {
		return nil, errors.New("GetIconInfo 失败")
	}
	defer func() {
		if ii.HbmColor != 0 {
			procDeleteObject.Call(ii.HbmColor)
		}
		if ii.HbmMask != 0 {
			procDeleteObject.Call(ii.HbmMask)
		}
	}()

	var bm winBitmap
	probe := ii.HbmColor
	if probe == 0 {
		probe = ii.HbmMask
	}
	if r0, _, _ := procGetObjectW.Call(probe, unsafe.Sizeof(bm), uintptr(unsafe.Pointer(&bm))); r0 == 0 {
		return nil, errors.New("GetObject 失败")
	}
	w, h := int(bm.BmWidth), int(bm.BmHeight)
	if w <= 0 || h <= 0 || w > 512 || h > 512 {
		return nil, errors.New("图标尺寸异常")
	}

	// 负高度 = 顶朝下，像素顺序与 PNG 一致，省一次翻转
	bi := bitmapInfo{}
	bi.Header.Size = uint32(unsafe.Sizeof(bi.Header))
	bi.Header.Width = int32(w)
	bi.Header.Height = -int32(h)
	bi.Header.Planes = 1
	bi.Header.BitCount = 32
	bi.Header.Compression = 0 // BI_RGB

	hdc, _, _ := procGetDC.Call(0)
	if hdc == 0 {
		return nil, errors.New("GetDC 失败")
	}
	defer procReleaseDC.Call(0, hdc)

	var bits unsafe.Pointer
	hbmp, _, _ := procCreateDIBSection.Call(
		hdc, uintptr(unsafe.Pointer(&bi)), dibRGBColors,
		uintptr(unsafe.Pointer(&bits)), 0, 0)
	if hbmp == 0 || bits == nil {
		return nil, errors.New("CreateDIBSection 失败")
	}
	defer procDeleteObject.Call(hbmp)

	memdc, _, _ := procCreateCompatibleDC.Call(hdc)
	if memdc == 0 {
		return nil, errors.New("CreateCompatibleDC 失败")
	}
	defer procDeleteDC.Call(memdc)

	old, _, _ := procSelectObject.Call(memdc, hbmp)
	procDrawIconEx.Call(memdc, 0, 0, hicon, uintptr(w), uintptr(h), 0, 0, diNormal)
	procSelectObject.Call(memdc, old)

	src := unsafe.Slice((*byte)(bits), w*h*4)
	img := image.NewNRGBA(image.Rect(0, 0, w, h))
	hasAlpha := false
	for i := 0; i < w*h; i++ {
		if src[i*4+3] != 0 {
			hasAlpha = true
			break
		}
	}
	for i := 0; i < w*h; i++ {
		img.Pix[i*4] = src[i*4+2]   // R <- BGRA
		img.Pix[i*4+1] = src[i*4+1] // G
		img.Pix[i*4+2] = src[i*4]   // B
		if hasAlpha {
			img.Pix[i*4+3] = src[i*4+3]
		} else {
			// 老式图标不带 alpha 通道，画出来会整张透明；补成不透明
			img.Pix[i*4+3] = 255
		}
	}

	var buf bytes.Buffer
	if err := png.Encode(&buf, img); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}
