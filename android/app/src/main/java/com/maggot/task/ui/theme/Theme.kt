package com.maggot.task.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/**
 * 这个产品**只有暗色主题**。标本缸不会因为你白天用就变成白的。
 * 但 Material3 的每个 role 都填满了，保证任何组件都不会掉回浅色默认值。
 */
private val MaggotScheme = darkColorScheme(
    primary = Primary,
    onPrimary = OnPrimary,
    primaryContainer = Color(0xFF2C4410),
    onPrimaryContainer = Fg,
    inversePrimary = Color(0xFF3D5C08),

    secondary = SecondaryCol,
    onSecondary = OnPrimary,
    secondaryContainer = Color(0xFF1C3B33),
    onSecondaryContainer = Fg,

    tertiary = Success,
    onTertiary = OnPrimary,

    background = Deep,
    onBackground = Fg,

    surface = SurfaceBg,
    onSurface = Fg,
    surfaceVariant = SurfaceHi,
    onSurfaceVariant = FgMuted,
    surfaceContainer = SurfaceBg,
    surfaceContainerHigh = SurfaceHi,
    surfaceContainerHighest = Color(0xFF232E1F),
    surfaceContainerLow = Color(0xFF0F150D),
    surfaceContainerLowest = Void,

    outline = BorderCol,
    outlineVariant = BorderHiCol,

    error = Danger,
    onError = Color.White,
    errorContainer = Color(0xFF4A1810),
    onErrorContainer = Color(0xFFFFD9D3),

    scrim = Color(0xCC000000),
)

@Composable
fun MaggotTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = MaggotScheme,
        typography = MaggotTypography,
        content = content,
    )
}
