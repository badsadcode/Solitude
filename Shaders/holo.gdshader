// Additive hologram lines: scanning bands, flicker, vertex-colour driven.
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

uniform float intensity = 1.1;
varying float local_y;

void vertex() {
	local_y = VERTEX.y;
}

void fragment() {
	float scan = 0.7 + 0.3 * sin(local_y * 140.0 - TIME * 5.0);
	float band = smoothstep(0.0, 0.02, abs(fract(local_y * 1.6 - TIME * 0.35) - 0.5));
	float flick = 0.92 + 0.08 * sin(TIME * 41.0) * sin(TIME * 13.7);
	ALBEDO = COLOR.rgb * COLOR.a * intensity * scan * flick * (0.75 + 0.25 * band);
}
