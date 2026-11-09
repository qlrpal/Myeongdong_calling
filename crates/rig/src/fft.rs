use crate::RigError;
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Complex {
    pub re: f64,
    pub im: f64,
}
impl Complex {
    pub(crate) fn mul(self, b: Self) -> Self {
        Self {
            re: self.re * b.re - self.im * b.im,
            im: self.re * b.im + self.im * b.re,
        }
    }
    pub(crate) fn magnitude(self) -> f64 {
        self.re.hypot(self.im)
    }
}
const BAD: RigError = RigError::Invalid("FFT length, index, or non-finite input");
pub(crate) fn zeros(n: usize) -> Result<Vec<Complex>, RigError> {
    let mut v = Vec::new();
    v.try_reserve_exact(n).map_err(RigError::Allocation)?;
    v.resize(n, Complex::default());
    Ok(v)
}
/// Radix-2 DFT; inverse uses the positive exponent and normalization by 1/N.
pub fn fft_in_place(buf: &mut [Complex], inverse: bool) -> Result<(), RigError> {
    let n = buf.len();
    if !n.is_power_of_two() || buf.iter().any(|v| !v.re.is_finite() || !v.im.is_finite()) {
        return Err(BAD);
    }
    let mut j = 0usize;
    for i in 1..n {
        let mut bit = n / 2;
        while j & bit != 0 {
            j ^= bit;
            bit >>= 1;
        }
        j ^= bit;
        if i < j {
            let a = *buf.get(i).ok_or(BAD)?;
            let b = *buf.get(j).ok_or(BAD)?;
            *buf.get_mut(i).ok_or(BAD)? = b;
            *buf.get_mut(j).ok_or(BAD)? = a;
        }
    }
    let mut width = 2usize;
    while width <= n {
        let half = width / 2;
        let angle = if inverse {
            std::f64::consts::TAU
        } else {
            -std::f64::consts::TAU
        } / width as f64;
        let root = Complex {
            re: angle.cos(),
            im: angle.sin(),
        };
        for chunk in buf.chunks_exact_mut(width) {
            let (a, b) = chunk.split_at_mut(half);
            let mut w = Complex { re: 1.0, im: 0.0 };
            for (a, b) in a.iter_mut().zip(b) {
                let t = b.mul(w);
                let u = *a;
                *a = Complex {
                    re: u.re + t.re,
                    im: u.im + t.im,
                };
                *b = Complex {
                    re: u.re - t.re,
                    im: u.im - t.im,
                };
                w = w.mul(root);
            }
        }
        if width == n {
            break;
        }
        width = width.checked_mul(2).ok_or(BAD)?;
    }
    if inverse {
        for z in buf.iter_mut() {
            z.re /= n as f64;
            z.im /= n as f64;
        }
    }
    if buf.iter().any(|z| !z.re.is_finite() || !z.im.is_finite()) {
        return Err(BAD);
    }
    Ok(())
}
/// Overlap-save FFT size is max(4096, next_power_of_two(2*template_len)).
/// Calls the visitor with valid correlation samples only, never a whole-recording array.
pub(crate) fn correlation_blocks(
    mut visit: impl FnMut(usize, &[Complex]) -> Result<(), RigError>,
    x: &[f32],
    t: &[Complex],
) -> Result<(), RigError> {
    if t.is_empty()
        || t.iter().any(|z| !z.re.is_finite() || !z.im.is_finite())
        || x.iter().any(|v| !v.is_finite())
    {
        return Err(BAD);
    }
    if t.len() > x.len() {
        return Ok(());
    }
    let n = t
        .len()
        .checked_mul(2)
        .and_then(usize::checked_next_power_of_two)
        .ok_or(BAD)?
        .max(4096);
    let overlap = t.len().checked_sub(1).ok_or(BAD)?;
    let step = n.checked_sub(overlap).ok_or(BAD)?;
    let count = x
        .len()
        .checked_sub(t.len())
        .and_then(|v| v.checked_add(1))
        .ok_or(BAD)?;
    let mut kernel = zeros(n)?;
    for (dst, src) in kernel.iter_mut().zip(t.iter().rev()) {
        *dst = Complex {
            re: src.re,
            im: -src.im,
        };
    }
    fft_in_place(&mut kernel, false)?;
    let mut work = zeros(n)?;
    let mut start = 0usize;
    while start < count {
        work.fill(Complex::default());
        for (dst, src) in work.iter_mut().zip(x.get(start..).ok_or(BAD)?) {
            dst.re = f64::from(*src);
        }
        fft_in_place(&mut work, false)?;
        for (v, k) in work.iter_mut().zip(&kernel) {
            *v = v.mul(*k);
        }
        fft_in_place(&mut work, true)?;
        let valid = step.min(count.checked_sub(start).ok_or(BAD)?);
        let end = overlap.checked_add(valid).ok_or(BAD)?;
        visit(start, work.get(overlap..end).ok_or(BAD)?)?;
        start = start.checked_add(valid).ok_or(BAD)?;
    }
    Ok(())
}
/// c(m) = sum x(m+n)*conj(template(n)), for every complete template window.
/// Empty templates are invalid; a template longer than x gives an empty result.
pub fn correlate_complex(x: &[f32], template: &[Complex]) -> Result<Vec<Complex>, RigError> {
    let mut out = Vec::new();
    if !template.is_empty() && template.len() <= x.len() {
        out.try_reserve_exact(
            x.len()
                .checked_sub(template.len())
                .and_then(|v| v.checked_add(1))
                .ok_or(BAD)?,
        )
        .map_err(RigError::Allocation)?;
    }
    correlation_blocks(
        |_, block| {
            out.extend_from_slice(block);
            Ok(())
        },
        x,
        template,
    )?;
    Ok(out)
}
