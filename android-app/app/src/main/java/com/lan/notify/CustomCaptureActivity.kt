package com.lan.notify

import com.journeyapps.barcodescanner.CaptureActivity
import com.journeyapps.barcodescanner.DecoratedBarcodeView

/**
 * 自定义扫码 Activity：加载 [R.layout.scan_capture]，把取景框缩小到固定
 * 280dp（默认 zxing 取景框约为屏幕宽 5/8，对二维码扫码来说过大）。
 */
class CustomCaptureActivity : CaptureActivity() {

    override fun initializeContent(): DecoratedBarcodeView {
        setContentView(R.layout.scan_capture)
        return findViewById(R.id.zxing_barcode_scanner)
    }
}
