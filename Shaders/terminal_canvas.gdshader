// The ship's log terminal shown full screen: a gentle CRT (slight curve, one scanline per text
// pixel row, soft glow at the edges) that keeps small text readable. Paper mode turns it all off.
shader_type canvas_item;

uniform bool crt = true;
uniform float curve = 0.06;

float hash(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

void fragment() {
	if (!crt) {
		COLOR = texture(TEXTURE, UV);
	} else {
		vec2 cc = UV - 0.5;
		vec2 uv = 0.5 + cc * (1.0 + curve * dot(cc, cc));
		float inside = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
		vec3 c = texture(TEXTURE, clamp(uv, vec2(0.0), vec2(1.0))).rgb;
		// A little bloom: neighbours bleed into dark pixels.
		vec2 px = vec2(1.0 / 640.0, 1.0 / 480.0);
		vec3 n = texture(TEXTURE, uv + vec2(px.x, 0.0)).rgb + texture(TEXTURE, uv - vec2(px.x, 0.0)).rgb;
		c = max(c, n * 0.22);
		c *= 0.86 + 0.14 * sin(uv.y * 480.0 * 3.14159);
		float frame = floor(mod(TIME * 18.0, 4096.0));
		c += vec3(hash(floor(UV * vec2(320.0, 240.0)) + vec2(frame * 1.3, frame * 0.7))) * 0.025;
		c *= (1.0 - 0.6 * smoothstep(0.5, 0.9, length(cc * 1.15))) * inside;
		COLOR = vec4(clamp(c * 1.1, 0.0, 1.0), 1.0);
	}
}
