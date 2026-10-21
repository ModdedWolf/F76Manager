function b64ToBytes(value) {
    const bin = atob(value || '');
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return bytes;
}

function b64ToF32(value) {
    const bytes = b64ToBytes(value);
    return new Float32Array(bytes.buffer, bytes.byteOffset, Math.floor(bytes.byteLength / 4));
}

function b64ToU32(value) {
    const bytes = b64ToBytes(value);
    return new Uint32Array(bytes.buffer, bytes.byteOffset, Math.floor(bytes.byteLength / 4));
}

function b64ToI32(value) {
    const bytes = b64ToBytes(value);
    return new Int32Array(bytes.buffer, bytes.byteOffset, Math.floor(bytes.byteLength / 4));
}

function multiply(a, b) {
    const out = new Float32Array(16);
    for (let c = 0; c < 4; c++) {
        for (let r = 0; r < 4; r++) {
            out[c * 4 + r] =
                a[r] * b[c * 4] +
                a[4 + r] * b[c * 4 + 1] +
                a[8 + r] * b[c * 4 + 2] +
                a[12 + r] * b[c * 4 + 3];
        }
    }
    return out;
}

function perspective(fov, aspect, near, far) {
    const f = 1 / Math.tan(fov / 2);
    const out = new Float32Array(16);
    out[0] = f / Math.max(aspect, 0.01);
    out[5] = f;
    out[10] = (far + near) / (near - far);
    out[11] = -1;
    out[14] = (2 * far * near) / (near - far);
    return out;
}

function lookAt(eye, center, up) {
    const z = normalize(sub(eye, center));
    const x = normalize(cross(up, z));
    const y = cross(z, x);
    return new Float32Array([
        x[0], y[0], z[0], 0,
        x[1], y[1], z[1], 0,
        x[2], y[2], z[2], 0,
        -dot(x, eye), -dot(y, eye), -dot(z, eye), 1
    ]);
}

function sub(a, b) { return [a[0] - b[0], a[1] - b[1], a[2] - b[2]]; }
function dot(a, b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
function cross(a, b) {
    return [
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0]
    ];
}
function normalize(v) {
    const len = Math.hypot(v[0], v[1], v[2]) || 1;
    return [v[0] / len, v[1] / len, v[2] / len];
}

const VERT = `#version 300 es
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNrm;
layout(location=2) in vec2 aUv;
uniform mat4 uMvp;
uniform float uPointSize;
out vec3 vNrm;
out vec2 vUv;
void main() {
    vNrm = aNrm;
    vUv = aUv;
    gl_Position = uMvp * vec4(aPos, 1.0);
    gl_PointSize = uPointSize;
}`;

const FRAG = `#version 300 es
precision mediump float;
in vec3 vNrm;
in vec2 vUv;
uniform sampler2D uTex;
uniform int uHasTex;
uniform int uWire;
out vec4 oColor;
void main() {
    vec3 n = normalize(vNrm);
    vec3 light = normalize(vec3(0.35, 0.55, 0.76));
    float lambert = max(dot(n, light), 0.0);
    vec3 base = uWire == 1 ? vec3(0.75, 0.82, 0.72) : (uHasTex == 1 ? texture(uTex, vUv).rgb : vec3(0.62, 0.64, 0.60));
    oColor = vec4(base * (0.28 + 0.72 * lambert), 1.0);
}`;

export function mountMeshViewer(container, mesh) {
    const canvas = document.createElement('canvas');
    canvas.tabIndex = 0;
    container.appendChild(canvas);
    const gl = canvas.getContext('webgl2', { antialias: true, alpha: false });
    if (!gl) {
        container.textContent = 'WebGL2 is not available.';
        return { dispose() {}, setWireframe() {}, reset() {} };
    }

    const program = createProgram(gl, VERT, FRAG);
    const uMvp = gl.getUniformLocation(program, 'uMvp');
    const uHasTex = gl.getUniformLocation(program, 'uHasTex');
    const uWire = gl.getUniformLocation(program, 'uWire');
    const uPointSize = gl.getUniformLocation(program, 'uPointSize');

    const textures = (mesh.textures || []).map(url => ({ url, tex: null, ready: false }));
    const shapes = (mesh.shapes || []).map(shape => buildShape(gl, shape));
    const bones = mesh.bones && mesh.bones.positions ? buildBones(gl, mesh.bones) : null;
    textures.forEach(item => loadTexture(gl, item, () => draw()));

    const bounds = mesh.bounds || { min: [0, 0, 0], max: [1, 1, 1] };
    const center0 = [
        (bounds.min[0] + bounds.max[0]) / 2,
        (bounds.min[1] + bounds.max[1]) / 2,
        (bounds.min[2] + bounds.max[2]) / 2
    ];
    const radius = Math.max(
        bounds.max[0] - bounds.min[0],
        bounds.max[1] - bounds.min[1],
        bounds.max[2] - bounds.min[2],
        0.001
    ) * 0.5;
    const initial = {
        yaw: 0.7,
        pitch: 0.45,
        distance: radius / Math.tan(Math.PI / 8) * 1.15,
        center: center0.slice()
    };
    const camera = {
        yaw: initial.yaw,
        pitch: initial.pitch,
        distance: initial.distance,
        center: initial.center.slice()
    };
    let wireframe = false;
    let disposed = false;

    function resize() {
        const rect = container.getBoundingClientRect();
        const dpr = Math.min(window.devicePixelRatio || 1, 2);
        const width = Math.max(1, Math.floor(rect.width * dpr));
        const height = Math.max(1, Math.floor(rect.height * dpr));
        if (canvas.width !== width || canvas.height !== height) {
            canvas.width = width;
            canvas.height = height;
        }
        draw();
    }

    function draw() {
        if (disposed) return;
        gl.viewport(0, 0, canvas.width, canvas.height);
        gl.clearColor(0.08, 0.075, 0.07, 1);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        gl.enable(gl.DEPTH_TEST);
        gl.useProgram(program);
        const eye = eyePosition(camera);
        const view = lookAt(eye, camera.center, [0, 0, 1]);
        const proj = perspective(Math.PI / 4, canvas.width / Math.max(canvas.height, 1), Math.max(radius * 0.01, 0.01), camera.distance + radius * 8);
        const mvp = multiply(proj, view);
        gl.uniformMatrix4fv(uMvp, false, mvp);
        gl.uniform1f(uPointSize, 1);
        gl.uniform1i(uWire, wireframe ? 1 : 0);
        for (const shape of shapes) {
            gl.bindVertexArray(shape.vao);
            const image = shape.texture >= 0 ? textures[shape.texture] : null;
            if (!wireframe && image?.ready) {
                gl.activeTexture(gl.TEXTURE0);
                gl.bindTexture(gl.TEXTURE_2D, image.tex);
                gl.uniform1i(uHasTex, 1);
            } else {
                gl.uniform1i(uHasTex, 0);
            }
            if (wireframe) {
                gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, shape.lines);
                gl.drawElements(gl.LINES, shape.lineCount, gl.UNSIGNED_INT, 0);
            } else {
                gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, shape.indices);
                gl.drawElements(gl.TRIANGLES, shape.indexCount, gl.UNSIGNED_INT, 0);
            }
        }
        if (bones) {
            gl.bindVertexArray(bones.vao);
            gl.uniform1i(uHasTex, 0);
            gl.uniform1i(uWire, 1);
            gl.uniform1f(uPointSize, 1);
            gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, bones.lines);
            gl.drawElements(gl.LINES, bones.lineCount, gl.UNSIGNED_INT, 0);
            gl.uniform1f(uPointSize, 6);
            gl.drawArrays(gl.POINTS, 0, bones.count);
        }
        gl.bindVertexArray(null);
    }

    const observer = new ResizeObserver(resize);
    observer.observe(container);

    let dragging = false;
    let panning = false;
    let lastX = 0;
    let lastY = 0;

    function viewAxes() {
        const eye = eyePosition(camera);
        const forward = normalize(sub(camera.center, eye));
        const right = normalize(cross(forward, [0, 0, 1]));
        const up = cross(right, forward);
        return { right, up };
    }

    function panBy(dx, dy) {
        const { right, up } = viewAxes();
        const scale = camera.distance * 0.002;
        camera.center[0] += (-dx * right[0] + dy * up[0]) * scale;
        camera.center[1] += (-dx * right[1] + dy * up[1]) * scale;
        camera.center[2] += (-dx * right[2] + dy * up[2]) * scale;
    }

    function zoomBy(factor, clientX, clientY) {
        const next = Math.max(radius * 0.05, Math.min(radius * 40, camera.distance * factor));
        const applied = next / camera.distance;
        if (clientX != null && clientY != null) {
            const rect = canvas.getBoundingClientRect();
            const ndcX = ((clientX - rect.left) / Math.max(rect.width, 1)) * 2 - 1;
            const ndcY = 1 - ((clientY - rect.top) / Math.max(rect.height, 1)) * 2;
            const { right, up } = viewAxes();
            const halfH = camera.distance * Math.tan(Math.PI / 8);
            const aspect = canvas.width / Math.max(canvas.height, 1);
            const shift = 1 - applied;
            camera.center[0] += (right[0] * ndcX * halfH * aspect + up[0] * ndcY * halfH) * shift;
            camera.center[1] += (right[1] * ndcX * halfH * aspect + up[1] * ndcY * halfH) * shift;
            camera.center[2] += (right[2] * ndcX * halfH * aspect + up[2] * ndcY * halfH) * shift;
        }
        camera.distance = next;
    }

    const onDown = (event) => {
        event.preventDefault();
        event.stopPropagation();
        dragging = true;
        panning = event.button === 1 || event.button === 2 || event.shiftKey;
        lastX = event.clientX;
        lastY = event.clientY;
        canvas.focus();
        if (canvas.setPointerCapture) canvas.setPointerCapture(event.pointerId);
    };
    const onMove = (event) => {
        if (!dragging) return;
        const dx = event.clientX - lastX;
        const dy = event.clientY - lastY;
        lastX = event.clientX;
        lastY = event.clientY;
        if (panning) {
            panBy(dx, dy);
        } else {
            camera.yaw += dx * 0.01;
            camera.pitch = Math.max(-1.2, Math.min(1.2, camera.pitch + dy * 0.01));
        }
        draw();
    };
    const onUp = () => { dragging = false; };
    const onWheel = (event) => {
        event.preventDefault();
        event.stopPropagation();
        zoomBy(event.deltaY > 0 ? 1.1 : 0.9, event.clientX, event.clientY);
        draw();
    };
    const onContext = (event) => {
        event.preventDefault();
        event.stopPropagation();
    };
    const onKey = (event) => {
        const step = 24;
        if (event.key === 'ArrowLeft') panBy(-step, 0);
        else if (event.key === 'ArrowRight') panBy(step, 0);
        else if (event.key === 'ArrowUp') panBy(0, -step);
        else if (event.key === 'ArrowDown') panBy(0, step);
        else if (event.key === '+' || event.key === '=') zoomBy(0.9);
        else if (event.key === '-' || event.key === '_') zoomBy(1.1);
        else return;
        event.preventDefault();
        draw();
    };
    const onDbl = () => {
        camera.yaw = initial.yaw;
        camera.pitch = initial.pitch;
        camera.distance = initial.distance;
        camera.center = initial.center.slice();
        draw();
    };
    canvas.addEventListener('pointerdown', onDown);
    window.addEventListener('pointermove', onMove);
    window.addEventListener('pointerup', onUp);
    canvas.addEventListener('wheel', onWheel, { passive: false });
    canvas.addEventListener('contextmenu', onContext);
    canvas.addEventListener('keydown', onKey);
    canvas.addEventListener('dblclick', onDbl);
    resize();

    return {
        setWireframe(next) {
            wireframe = Boolean(next);
            draw();
        },
        reset: onDbl,
        dispose() {
            if (disposed) return;
            disposed = true;
            observer.disconnect();
            canvas.removeEventListener('pointerdown', onDown);
            window.removeEventListener('pointermove', onMove);
            window.removeEventListener('pointerup', onUp);
            canvas.removeEventListener('wheel', onWheel);
            canvas.removeEventListener('contextmenu', onContext);
            canvas.removeEventListener('keydown', onKey);
            canvas.removeEventListener('dblclick', onDbl);
            for (const shape of shapes) {
                gl.deleteBuffer(shape.positions);
                gl.deleteBuffer(shape.normals);
                gl.deleteBuffer(shape.uvs);
                gl.deleteBuffer(shape.indices);
                gl.deleteBuffer(shape.lines);
                gl.deleteVertexArray(shape.vao);
            }
            if (bones) {
                gl.deleteBuffer(bones.positions);
                gl.deleteBuffer(bones.normals);
                gl.deleteBuffer(bones.uvs);
                gl.deleteBuffer(bones.lines);
                gl.deleteVertexArray(bones.vao);
            }
            for (const item of textures) {
                if (item.tex) gl.deleteTexture(item.tex);
            }
            gl.deleteProgram(program);
            const lose = gl.getExtension('WEBGL_lose_context');
            if (lose) lose.loseContext();
            canvas.remove();
        }
    };
}

function eyePosition(camera) {
    const cp = Math.cos(camera.pitch);
    const sp = Math.sin(camera.pitch);
    return [
        camera.center[0] + camera.distance * cp * Math.sin(camera.yaw),
        camera.center[1] + camera.distance * cp * Math.cos(camera.yaw),
        camera.center[2] + camera.distance * sp
    ];
}

function buildShape(gl, shape) {
    const positions = b64ToF32(shape.positions);
    const count = Math.floor(positions.length / 3);
    let normals = shape.normals ? b64ToF32(shape.normals) : null;
    if (!normals || normals.length < count * 3) {
        normals = new Float32Array(count * 3);
        for (let i = 0; i < count; i++) normals[i * 3 + 2] = 1;
    }
    let uvs = shape.uvs ? b64ToF32(shape.uvs) : null;
    if (!uvs || uvs.length < count * 2) uvs = new Float32Array(count * 2);
    const indices = b64ToU32(shape.indices);
    const lines = lineIndices(indices);

    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    const posBuf = buffer(gl, gl.ARRAY_BUFFER, positions);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
    const nrmBuf = buffer(gl, gl.ARRAY_BUFFER, normals);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 0, 0);
    const uvBuf = buffer(gl, gl.ARRAY_BUFFER, uvs);
    gl.enableVertexAttribArray(2);
    gl.vertexAttribPointer(2, 2, gl.FLOAT, false, 0, 0);
    const indexBuf = buffer(gl, gl.ELEMENT_ARRAY_BUFFER, indices);
    const lineBuf = buffer(gl, gl.ELEMENT_ARRAY_BUFFER, lines);
    gl.bindVertexArray(null);

    return {
        vao,
        positions: posBuf,
        normals: nrmBuf,
        uvs: uvBuf,
        indices: indexBuf,
        lines: lineBuf,
        indexCount: indices.length,
        lineCount: lines.length,
        texture: Number.isInteger(shape.texture) ? shape.texture : -1
    };
}

function buildBones(gl, bones) {
    const positions = b64ToF32(bones.positions);
    const parents = b64ToI32(bones.parents);
    const count = Math.floor(positions.length / 3);
    const normals = new Float32Array(count * 3);
    for (let i = 0; i < count; i++) normals[i * 3 + 2] = 1;
    const lines = [];
    for (let i = 0; i < count && i < parents.length; i++) {
        const parent = parents[i];
        if (parent >= 0 && parent < count && parent !== i) {
            lines.push(i, parent);
        }
    }
    const lineData = new Uint32Array(lines);
    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    const posBuf = buffer(gl, gl.ARRAY_BUFFER, positions);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
    const nrmBuf = buffer(gl, gl.ARRAY_BUFFER, normals);
    gl.enableVertexAttribArray(1);
    gl.vertexAttribPointer(1, 3, gl.FLOAT, false, 0, 0);
    const uvBuf = buffer(gl, gl.ARRAY_BUFFER, new Float32Array(count * 2));
    gl.enableVertexAttribArray(2);
    gl.vertexAttribPointer(2, 2, gl.FLOAT, false, 0, 0);
    const lineBuf = buffer(gl, gl.ELEMENT_ARRAY_BUFFER, lineData);
    gl.bindVertexArray(null);
    return { vao, positions: posBuf, normals: nrmBuf, uvs: uvBuf, lines: lineBuf, lineCount: lineData.length, count };
}

function lineIndices(indices) {
    const lines = new Uint32Array(indices.length * 2);
    let o = 0;
    for (let i = 0; i + 2 < indices.length; i += 3) {
        const a = indices[i];
        const b = indices[i + 1];
        const c = indices[i + 2];
        lines[o++] = a; lines[o++] = b;
        lines[o++] = b; lines[o++] = c;
        lines[o++] = c; lines[o++] = a;
    }
    return lines;
}

function buffer(gl, target, data) {
    const buf = gl.createBuffer();
    gl.bindBuffer(target, buf);
    gl.bufferData(target, data, gl.STATIC_DRAW);
    return buf;
}

function loadTexture(gl, item, onReady) {
    const image = new Image();
    image.onload = () => {
        const tex = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, tex);
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, 0);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, image);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
        gl.generateMipmap(gl.TEXTURE_2D);
        item.tex = tex;
        item.ready = true;
        onReady();
    };
    image.src = item.url;
}

function createProgram(gl, vertSrc, fragSrc) {
    const program = gl.createProgram();
    const vert = compile(gl, gl.VERTEX_SHADER, vertSrc);
    const frag = compile(gl, gl.FRAGMENT_SHADER, fragSrc);
    gl.attachShader(program, vert);
    gl.attachShader(program, frag);
    gl.linkProgram(program);
    gl.deleteShader(vert);
    gl.deleteShader(frag);
    return program;
}

function compile(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    return shader;
}
