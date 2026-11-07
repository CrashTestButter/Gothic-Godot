// Gothic world: texture array layer from UV2.x, Gothic's baked vertex light in COLOR.
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque;

uniform sampler2DArray albedo_array : source_color, filter_linear_mipmap, repeat_enable;
uniform float light_scale = 1.0;

varying flat float layer;

void vertex() {
	layer = UV2.x;
}

void fragment() {
	vec4 tex = texture(albedo_array, vec3(UV, layer));
	ALBEDO = tex.rgb * COLOR.rgb * light_scale;
}
