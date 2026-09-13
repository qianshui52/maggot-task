package com.maggot.task

import android.app.Application
import android.content.Context
import com.maggot.task.diag.CrashLog

class MaggotApp : Application() {

    val graph: AppGraph by lazy { AppGraph(this) }

    override fun onCreate() {
        super.onCreate()

        // ① 先装崩溃记录器，再干任何别的事。
        //    顺序不能反：下面两行都可能在真机上抛异常，而记录器必须已经在位。
        //    没有它，用户就只能提供"点进去就闪退"这种无法定位的描述。
        CrashLog.install(this)

        // ①′ 注意：这里**不能** markBootStart。
        //    启动标记必须由 MainActivity 在「读完上一次的死亡记录之后」再写 ——
        //    如果在这里先写 starting，Activity 就再也读不到上一次留下的状态了
        //    （1.2.4 正是犯了这个错：每次冷启动都误报"上次启动没有走完"，
        //    而且报告页永远关不掉）。Application → Activity 之间几毫秒的窗口
        //    就算了 —— 那种死法连 logcat 都抓不到，不值得为它破坏读序。

        // ② 冷启动恢复。
        //    这里**必须**捕获异常：`Application.onCreate` 抛出去 = 进程直接死，
        //    用户看到的就是"一闪就没了 + 黑屏"，连是哪一步坏的都无从得知。
        //    现在失败会降级成「控制台能打开 + 顶上显示错误」。
        graph.bootstrapSafely()

        // ③ 启动路径上的历史错误也在这里落一次（若 bootstrap 内部失败）。
        graph.lastStartupError?.let { CrashLog.recordNonFatal(this, "bootstrap", it) }
    }
}

/** 从任意 Context 拿到全局依赖容器。 */
val Context.appGraph: AppGraph
    get() = (applicationContext as MaggotApp).graph
