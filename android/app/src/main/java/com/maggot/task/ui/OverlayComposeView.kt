package com.maggot.task.ui

import android.content.Context
import android.view.Choreographer
import androidx.compose.runtime.Composable
import androidx.compose.runtime.MonotonicFrameClock
import androidx.compose.runtime.Recomposer
import androidx.compose.ui.platform.AbstractComposeView
import androidx.compose.ui.platform.AndroidUiDispatcher
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.ViewModelStoreOwner
import androidx.lifecycle.setViewTreeLifecycleOwner
import androidx.savedstate.SavedStateRegistry
import androidx.savedstate.SavedStateRegistryController
import androidx.savedstate.SavedStateRegistryOwner
import androidx.savedstate.setViewTreeSavedStateRegistryOwner
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine

/**
 * 通过 WindowManager 添加的悬浮窗**不在任何 Activity 的 ViewTree 上**，
 * 所以 ComposeView 找不到宿主，会直接抛异常。
 * 这里自己实现三个 owner，是官方推荐的悬浮窗 + Compose 方案。
 */
class OverlayComposeView(
    context: Context,
    private val content: @Composable () -> Unit,
) : AbstractComposeView(context),
    LifecycleOwner,
    ViewModelStoreOwner,
    SavedStateRegistryOwner {

    private val lifecycleRegistry = LifecycleRegistry(this)
    private val savedStateController = SavedStateRegistryController.create(this)
    private val store = ViewModelStore()

    override val lifecycle: Lifecycle get() = lifecycleRegistry

    override val viewModelStore: ViewModelStore get() = store

    override val savedStateRegistry: SavedStateRegistry
        get() = savedStateController.savedStateRegistry

    // ────────────────────────── 自管 recomposer ──────────────────────────
    // ⚠️ 必须声明在 init 块**之前** —— Kotlin 按声明顺序初始化属性。

    /** Choreographer 驱动的帧时钟 —— 主线程安全，不依赖任何 Activity/Lifecycle。 */
    private val frameClock = object : MonotonicFrameClock {
        override suspend fun <R> withFrameNanos(onFrame: (Long) -> R): R =
            suspendCancellableCoroutine { cont ->
                Choreographer.getInstance().postFrameCallback { frameTimeNanos ->
                    try {
                        if (cont.isActive) cont.resumeWith(Result.success(onFrame(frameTimeNanos)))
                    } catch (t: Throwable) {
                        if (cont.isActive) cont.resumeWith(Result.failure(t))
                    }
                }
                cont.invokeOnCancellation {
                    // 无法按帧精确反注册，但 callback 里的 isActive 检查会丢弃过期帧。
                }
            }
    }

    private val recomposerScope =
        CoroutineScope(SupervisorJob() + AndroidUiDispatcher.CurrentThread + frameClock)

    private val recomposer = Recomposer(
        SupervisorJob() + AndroidUiDispatcher.CurrentThread + frameClock,
    )

    init {
        savedStateController.performRestore(null)
        lifecycleRegistry.currentState = Lifecycle.State.RESUMED

        // ⚠️ 这里是 1.2.5 崩溃（也是 1.2.1 以来「一点上桌就闪退」）的真正修复点。
        //
        // 只「实现」LifecycleOwner / SavedStateRegistryOwner 是**不够**的：
        // Compose 建 recomposer 时走的是 ViewTreeLifecycleOwner.get(rootView)，
        // 它只查 View 上的 **tag**，不会做 `view is LifecycleOwner` 的类型判断。
        // WindowManager 挂载的窗口没有 Activity 替你把这些 owner 写进 tag，
        // 于是 attach 时抛 IllegalStateException —— 而且是在 Choreographer 的
        // traversal 回调里异步抛的，addView 外面的 try/catch 根本接不住，
        // 主线程一炸整个进程就没了。
        // 把自己显式 set 进 tag，才是这个场景的完整写法。
        // lifecycle 2.8 / savedstate 1.2 的公开 API 是 View 扩展函数
        // （object 形式的 ViewTreeLifecycleOwner.set 已被 @Hidden 标记，不能直接调）。
        setViewTreeLifecycleOwner(this)
        setViewTreeSavedStateRegistryOwner(this)

        // ⚠️ 1.2.9：自管 recomposer，不再走 createLifecycleAwareWindowRecomposer。
        //
        // 1.2.8 的实机面包屑证明触摸能到达本 View，但按钮的点击处理器不执行；
        // 拖动（pointerInput）却正常 —— 症状指向 lifecycle 感知 recomposer 那套
        // PausableMonotonicFrameClock 在悬浮窗场景下不可靠（它把帧时钟挂在
        // 合成的 LifecycleOwner 上，任何一次 pause/resume 时序偏差都会让
        // 重组或点击响应停摆，且表面上看不出任何异常）。
        // 这里完全绕开它：自己的 Recomposer + 自己的 Choreographer 帧时钟，
        // 通过 setParentCompositionContext 注入 —— resolveParentCompositionContext
        // 看到非空 parentContext 就不会再去找 ViewTreeLifecycleOwner。
        // 这是"无 Activity 宿主"场景下最可控的方案。
        setParentCompositionContext(recomposer)
        recomposerScope.launch { recomposer.runRecomposeAndApplyChanges() }

        // 默认的 ViewCompositionStrategy 就是 DisposeOnDetachedFromWindowOrReleasedFromPool：
        // 从 WindowManager 移除时自动释放 composition，正是我们要的行为。
    }

    @Composable
    override fun Content() {
        content()
    }

    /** 移除悬浮窗之前调用，确保资源被回收。 */
    fun destroy() {
        lifecycleRegistry.currentState = Lifecycle.State.DESTROYED
        store.clear()
    }

    // ────────────────────────── 触摸 breadcrumbs ──────────────────────────

    /**
     * 「面板按钮点不到」这种问题没有 logcat 就永远无解 —— 用户只能说出结果，
     * 说不出事件死在哪一层。这里把**到达本 View 的前几次触摸**记进诊断日志：
     * 下次报告里若一条都没有 → 事件根本没进窗口（z 序/标志位问题）；
     * 若有但按钮没反应 → 事件进了 Compose 却被消费逻辑吞了（我们的问题）。
     * 一条面包屑就能把两类原因分开。
     */
    private var touchBreadcrumbs = 0

    override fun dispatchTouchEvent(ev: android.view.MotionEvent): Boolean {
        if (touchBreadcrumbs < 6) {
            touchBreadcrumbs++
            val action = when (ev.actionMasked) {
                android.view.MotionEvent.ACTION_DOWN -> "DOWN"
                android.view.MotionEvent.ACTION_UP -> "UP"
                android.view.MotionEvent.ACTION_MOVE -> "MOVE"
                android.view.MotionEvent.ACTION_CANCEL -> "CANCEL"
                else -> "OTHER"
            }
            if (action != "MOVE") {
                com.maggot.task.diag.CrashLog.recordNote(
                    this.context,
                    "面板触摸#$touchBreadcrumbs $action (${ev.x.toInt()},${ev.y.toInt()})",
                )
            }
        }
        return super.dispatchTouchEvent(ev)
    }
}
