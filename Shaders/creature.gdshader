// PS1 creature material: vertex snapping, nearest texels, wet sheen, and emission
// derived from the albedo itself (the brightest texels — the eyes — glow).
// mask_tex is the same texture sampled raw (sRGB values, no linearisation) so the
// threshold matches what you see in the image.
shader_type spatial;
render_mode diffuse_lambert, specular_schlick_ggx, cull_disabled;

uniform sampler2D albedo_tex : source_color, filter_nearest, repeat_enable;
uniform sampler2D mask_tex : filter_nearest, repeat_enable;
uniform vec4 tint : source_color = vec4(1.0);
uniform float jitter = 200.0;
uniform float roughness_v : hint_range(0.0, 1.0) = 0.45;
uniform float wet_spec : hint_range(0.0, 1.0) = 0.6;
uniform vec4 eye_color : source_color = vec4(1.0, 0.86, 0.6, 1.0);
uniform float eye_energy = 16.0;
uniform float eye_threshold = 0.62;
uniform float eye_open = 1.0; // 0 = eyes dark (blink / hidden)
// Eye centres in model space (measured from the ravager mesh). Only bright texels near
// these glow; other bright texels (teeth) glow at teeth_glow strength.
uniform vec3 eye_l = vec3(-0.057, 0.044, 0.368);
uniform vec3 eye_r = vec3(0.054, 0.043, 0.370);
uniform float eye_radius = 0.035;
uniform float teeth_glow = 0.0;
// Models that ship a real emission map (Fangwyrm, Crystal Ray) use it instead of the derived eyes.
uniform bool use_emission_tex = false;
uniform sampler2D emission_tex : source_color, filter_nearest, hint_default_black;
uniform float emission_tex_energy = 3.0;

varying vec3 model_pos;

void vertex() {
	model_pos = VERTEX; // skinned, model space
	vec4 clip = PROJECTION_MATRIX * (MODELVIEW_MATRIX * vec4(VERTEX, 1.0));
	if (jitter > 0.0 && clip.w > 0.0001) {
		vec2 grid = vec2(jitter, jitter * 0.5625);
		vec2 ndc = floor(clip.xy / clip.w * grid + 0.5) / grid;
		clip.xy = ndc * clip.w;
	}
	POSITION = clip;
}

void fragment() {
	vec3 a = texture(albedo_tex, UV).rgb;
	vec3 raw = texture(mask_tex, UV).rgb;
	float l = dot(raw, vec3(0.3, 0.59, 0.11));
	float m = smoothstep(eye_threshold, eye_threshold + 0.08, l);
	ALBEDO = a * tint.rgb;
	ROUGHNESS = roughness_v;
	SPECULAR = wet_spec;
	float pulse = 0.85 + 0.15 * sin(TIME * 1.7);
	float near_eye = 1.0 - smoothstep(eye_radius * 0.6, eye_radius, min(distance(model_pos, eye_l), distance(model_pos, eye_r)));
	float glow = m * mix(teeth_glow, 1.0, near_eye);
	EMISSION = eye_color.rgb * glow * eye_energy * pulse * eye_open;
	if (use_emission_tex) {
		EMISSION = texture(emission_tex, UV).rgb * emission_tex_energy * pulse * eye_open;
	}
}
