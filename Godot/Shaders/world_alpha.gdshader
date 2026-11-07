// Alpha tested variant (plants, fences). Kept separate: discard costs early depth on tile based GPUs.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque;

uniform sampler2DArray albedo_array : source_color, filter_linear_mipmap, repeat_enable;
uniform float light_scale = 1.0;
// Daylight on outdoor surfaces (COLOR.a = 1), set by the time of day; inside buildings stays as baked.
uniform float sky_light = 1.0;

varying flat float layer;

void vertex() {
	layer = UV2.x;
}

void fragment() {
	vec4 tex = texture(albedo_array, vec3(UV, layer));
	ALBEDO = tex.rgb * COLOR.rgb * light_scale * mix(1.0, sky_light, COLOR.a);
	ALPHA = tex.a;
	ALPHA_SCISSOR_THRESHOLD = 0.5;
}
